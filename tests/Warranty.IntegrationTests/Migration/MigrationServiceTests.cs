using Azure.Storage.Blobs;
using Npgsql;
using Testcontainers.Azurite;
using Testcontainers.PostgreSql;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Migration;

/// <summary>
/// Runs the real migration service composition against PostgreSQL with pgvector and Azurite (hash
/// embeddings instead of Ollama), twice, and checks the schema, the seed, the knowledge index and the
/// app role — and that the second run changes nothing.
/// </summary>
public sealed class MigrationServiceTests : IAsyncLifetime
{
    private const string AppRolePassword = "integration-app-password";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
        .WithCommand("--skipApiVersionCheck")
        .Build();

    public async ValueTask InitializeAsync()
        => await Task.WhenAll(_postgres.StartAsync(TestContext.Current.CancellationToken), _azurite.StartAsync(TestContext.Current.CancellationToken));

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _azurite.DisposeAsync();
    }

    [Fact]
    public async Task Migrates_seeds_and_indexes_knowledge_and_a_second_run_changes_nothing()
    {
        (await RunMigrationServiceAsync()).ShouldBe(0);
        var first = await CountsAsync();

        first.ShouldBe(new Dictionary<string, long>
        {
            ["tenancy.tenants"] = 2,
            ["tenancy.tenant_channels"] = 2,
            ["tenancy.tenant_settings"] = 2,
            ["catalog.products"] = 7,
            ["catalog.product_serials"] = 135,
            ["crm.customers"] = 11,
            ["integration.service_centers"] = 8,
            ["claims.claims"] = 12,
            ["audit.decision_trail_entries"] = 49,
            ["claims.claim_remedies"] = 9,
            ["claims.remedy_decisions"] = 18,
            ["claims.repairs"] = 7,
            ["claims.repair_handovers"] = 7,
            ["audit.repair_events"] = 7,
            ["policy.warranty_policies"] = 2,
            ["policy.policy_versions"] = 3,
        });
        (await KnowledgeAsync()).ShouldBe(new Dictionary<string, long>
        {
            ["global"] = 4,
            ["tenant-aurora"] = 2,
            ["tenant-borealis"] = 1,
        });

        (await RunMigrationServiceAsync()).ShouldBe(0);
        (await CountsAsync()).ShouldBe(first);
        (await KnowledgeAsync()).Values.Sum().ShouldBe(7);
    }

    [Fact]
    public async Task History_claims_are_finalized_relative_to_the_seeding_day_with_a_seeded_trail()
    {
        (await RunMigrationServiceAsync()).ShouldBe(0);

        await using var warranty = await OpenAsync("warranty");
        await using var command = new NpgsqlCommand(
            """
            select c.serial_number, c.finalized_at, count(t.*) as entries, bool_and((t.payload::jsonb ->> 'seededHistory') = 'true') as marked
            from claims.claims c join audit.decision_trail_entries t on t.claim_id = c.id
            where c.reference like 'HSTRY%'
            group by c.serial_number, c.finalized_at order by c.serial_number
            """,
            warranty);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<(string Serial, DateTimeOffset FinalizedAt, long Entries, bool Marked)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetInt64(2), reader.GetBoolean(3)));
        }

        rows.Select(r => r.Serial).ShouldBe(["AT10-24-0013", "AT10-24-0014"]);
        (DateTimeOffset.UtcNow - rows[0].FinalizedAt).TotalDays.ShouldBe(30, tolerance: 0.1);
        (DateTimeOffset.UtcNow - rows[1].FinalizedAt).TotalDays.ShouldBe(120, tolerance: 0.1);
        rows.ShouldAllBe(r => r.Entries == 2 && r.Marked);
    }

    /// <summary>
    /// Feature 002: every policy version gets its tenant's remedy rules (tasks.md "Fixed identifiers"), stored
    /// as the schema's JSON, and every region of every tenant has at least two active repair centres with the
    /// address and opening hours claimants see (FR-045).
    /// </summary>
    [Fact]
    [Trait("Feature", "002")]
    public async Task Seeds_the_remedy_rules_of_each_policy_version_and_two_active_repair_centres_per_region()
    {
        (await RunMigrationServiceAsync()).ShouldBe(0);
        await using var warranty = await OpenAsync("warranty");

        const string aurora =
            """{ "coveredDefect": { "remedy": "Repair" }, "repairFailed": { "afterFailedAttempts": 2, "remedy": "Replacement" }, "protectionPlan": { "liftsAccidentalDamageExclusion": false }, "replacement": { "productRule": "SameCategoryAtLeastClaimValue", "returnRule": "AtCollection", "minimumCoverMonths": 0 }, "replacementNotAvailable": { "remedy": "Refund" } }""";
        const string borealis =
            """{ "coveredDefect": { "remedy": "Repair" }, "repairNotEconomical": { "limitPercentOfClaimValue": 60, "allowed": ["Refund", "Replacement"] }, "repairFailed": { "afterFailedAttempts": 2, "remedy": "Refund" }, "protectionPlan": { "liftsAccidentalDamageExclusion": true, "remedy": "Repair" }, "replacement": { "productRule": "SameModel", "returnRule": "BeforeReservation", "minimumCoverMonths": 6 }, "replacementNotAvailable": { "remedy": "Refund" } }""";
        await using (var versions = new NpgsqlCommand(
                         """
                         select p.code || ' v' || v.version,
                                v.remedy_rules::jsonb = (case p.code when 'AUR-WP' then @aurora else @borealis end)::jsonb
                         from policy.policy_versions v join policy.warranty_policies p on p.id = v.policy_id
                         order by 1
                         """,
                         warranty))
        {
            versions.Parameters.AddWithValue("aurora", aurora);
            versions.Parameters.AddWithValue("borealis", borealis);
            await using var reader = await versions.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            var rows = new List<(string Version, bool Matches)>();
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                rows.Add((reader.GetString(0), !reader.IsDBNull(1) && reader.GetBoolean(1)));
            }

            rows.Select(r => r.Version).ShouldBe(["AUR-WP v1", "AUR-WP v2", "BOR-WP v1"]);
            rows.Where(r => !r.Matches).Select(r => r.Version).ShouldBeEmpty("versions whose remedy rules differ from the fixed rule set");
        }

        (await ScalarAsync(
                warranty,
                """
                select count(*) from (
                    select tenant_id, region from integration.service_centers group by tenant_id, region
                    having count(*) filter (where is_active) < 2) regions
                """))
            .ShouldBe(0, "tenant regions with fewer than two active repair centres");
        (await ScalarAsync(
                warranty,
                """
                select count(*) from integration.service_centers
                where is_active and (coalesce(trim(address), '') = '' or coalesce(trim(opening_hours), '') = '')
                """))
            .ShouldBe(0, "active repair centres without address or opening hours");
    }

    /// <summary>
    /// Feature 003 (T313, FR-037): every seeded policy version stores the replacement rules and the
    /// "replacement not available" row of its tenant (tasks.md "Fixed identifiers").
    /// </summary>
    [Fact]
    [Trait("Feature", "003")]
    public async Task Seeds_the_replacement_rules_of_each_policy_version()
    {
        (await RunMigrationServiceAsync()).ShouldBe(0);
        await using var warranty = await OpenAsync("warranty");

        await using var command = new NpgsqlCommand(
            """
            select p.code || ' v' || v.version,
                   v.remedy_rules -> 'replacement' ->> 'productRule',
                   v.remedy_rules -> 'replacement' ->> 'returnRule',
                   (v.remedy_rules -> 'replacement' ->> 'minimumCoverMonths')::int,
                   v.remedy_rules -> 'replacementNotAvailable' ->> 'remedy'
            from policy.policy_versions v join policy.warranty_policies p on p.id = v.policy_id
            order by 1
            """,
            warranty);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<(string, string, string, int, string)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4)));
        }

        rows.ShouldBe(
        [
            ("AUR-WP v1", "SameCategoryAtLeastClaimValue", "AtCollection", 0, "Refund"),
            ("AUR-WP v2", "SameCategoryAtLeastClaimValue", "AtCollection", 0, "Refund"),
            ("BOR-WP v1", "SameModel", "BeforeReservation", 6, "Refund"),
        ]);
    }

    /// <summary>
    /// Feature 003 (FR-038, research R14): each region of each tenant has an active centre that issues replacement
    /// units for every product category the tenant sells, and the seeded catalog holds Aurora's AUR-TAB11.
    /// </summary>
    [Fact(Skip = "Pending T312")]
    [Trait("Feature", "003")]
    public async Task Seeds_a_replacement_location_for_every_region_and_product_category()
    {
        (await RunMigrationServiceAsync()).ShouldBe(0);
        await using var warranty = await OpenAsync("warranty");

        (await ScalarAsync(warranty, "select count(*) from integration.service_centers where is_active and issues_replacements"))
            .ShouldBeGreaterThan(0, "no active centre issues replacements");
        (await ScalarAsync(
                warranty,
                """
                select count(*) from (
                    select distinct s.tenant_id, s.region, p.category
                    from integration.service_centers s
                    join catalog.products p on p.tenant_id = s.tenant_id
                    where not exists (
                        select 1 from integration.service_centers l
                        where l.tenant_id = s.tenant_id and l.region = s.region
                          and l.is_active and l.issues_replacements and p.category = any (l.capabilities))) uncovered
                """))
            .ShouldBe(0, "tenant regions and product categories without a replacement location");
        (await ScalarAsync(
                warranty,
                """
                select count(*) from catalog.products p join tenancy.tenants t on t.id = p.tenant_id
                where t.slug = 'aurora' and p.model_code = 'AUR-TAB11' and p.category = 'tablet' and p.claim_value = 520
                """))
            .ShouldBe(1, "Aurora's equivalent tablet AUR-TAB11");
    }

    /// <summary>
    /// Feature 002 (T222): the remedy scenario claims are Approved by the system at their historical time, and their
    /// remedy is the one the evaluator derives from the claim's own policy version — Repair set by policy from row
    /// <c>coveredDefect</c>, with an evaluation and a policy decision row — and a repair awaiting hand-over at the
    /// first eligible centre of the claim's region (priority, then name). Every trail entry is marked seeded history.
    /// </summary>
    [Fact]
    [Trait("Feature", "002")]
    public async Task Remedy_scenario_claims_are_approved_with_the_remedy_of_their_own_policy_version()
    {
        (await RunMigrationServiceAsync()).ShouldBe(0);
        await using var warranty = await OpenAsync("warranty");
        await using var command = new NpgsqlCommand(
            """
            select c.reference, c.status, c.final_decided_by, p.code || ' v' || v.version, r.state, r.remedy, r.decided_rows,
                   rp.status, sc.name, c.finalized_at,
                   (select count(*) from audit.decision_trail_entries t where t.claim_id = c.id),
                   (select bool_and((t.payload::jsonb ->> 'seededHistory') = 'true') from audit.decision_trail_entries t where t.claim_id = c.id)
            from claims.claims c
            join (select cr.claim_id, cr.state, cr.remedy, cr.policy_version_id,
                         (select string_agg(d.kind || ':' || d.table_row, ',' order by d.seq) from claims.remedy_decisions d where d.claim_id = cr.claim_id) as decided_rows
                  from claims.claim_remedies cr) r on r.claim_id = c.id
            join policy.policy_versions v on v.id = r.policy_version_id
            join policy.warranty_policies p on p.id = v.policy_id
            join claims.repairs rp on rp.claim_id = c.id
            join claims.repair_handovers h on h.id = rp.active_handover_id
            join integration.service_centers sc on sc.id = h.service_center_id
            where c.reference like 'RMDY%'
            order by c.reference
            """,
            warranty);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            (DateTimeOffset.UtcNow - reader.GetFieldValue<DateTimeOffset>(9)).TotalDays.ShouldBeGreaterThan(2.9);
            reader.GetInt64(10).ShouldBe(5);
            reader.GetBoolean(11).ShouldBeTrue();
            rows.Add(string.Join(" | ", Enumerable.Range(0, 9).Select(i => Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        }

        rows.ShouldBe(
        [
            "RMDYR10B01 | Approved | System | BOR-WP v1 | InProgress | Repair | Evaluated:coveredDefect,Decided:coveredDefect | AwaitingHandover | Borealis Repair Hub Lyon",
            "RMDYR12A01 | Approved | System | AUR-WP v2 | InProgress | Repair | Evaluated:coveredDefect,Decided:coveredDefect | AwaitingHandover | Aurora Service Center Springfield",
            "RMDYR1A001 | Approved | System | AUR-WP v2 | InProgress | Repair | Evaluated:coveredDefect,Decided:coveredDefect | AwaitingHandover | Aurora Service Center Springfield",
            "RMDYR2B001 | Approved | System | BOR-WP v1 | InProgress | Repair | Evaluated:coveredDefect,Decided:coveredDefect | AwaitingHandover | Borealis Repair Hub Lyon",
            "RMDYR4A001 | Approved | System | AUR-WP v2 | InProgress | Repair | Evaluated:coveredDefect,Decided:coveredDefect | AwaitingHandover | Aurora Service Center Amsterdam",
            "RMDYR4B001 | Approved | System | BOR-WP v1 | InProgress | Repair | Evaluated:coveredDefect,Decided:coveredDefect | AwaitingHandover | Borealis Repair Hub Lyon",
            "RMDYR9B001 | Approved | System | BOR-WP v1 | InProgress | Repair | Evaluated:coveredDefect,Decided:coveredDefect | AwaitingHandover | Borealis Repair Hub Lyon",
        ]);
    }

    /// <summary>
    /// Feature 002 (T251, R11): the claims with a decided remedy get it from a re-evaluation of their own policy version
    /// after two failed repairs (row <c>repairFailed</c>) and a seeded-history reviewer decision with its reason —
    /// Replacement at Aurora, Refund at Borealis — in progress, with no repair, no completion and no refund request.
    /// </summary>
    [Fact]
    [Trait("Feature", "002")]
    public async Task Decided_remedy_claims_get_the_reviewer_decided_remedy_of_their_own_policy_version()
    {
        (await RunMigrationServiceAsync()).ShouldBe(0);
        await using var warranty = await OpenAsync("warranty");
        await using var command = new NpgsqlCommand(
            """
            select c.reference, c.status, p.code || ' v' || v.version, r.scenario, r.state, r.remedy,
                   (select string_agg(d.kind || ':' || d.table_row || ':' || coalesce(d.decided_by, '-') || ':' || d.failed_attempts, ',' order by d.seq)
                    from claims.remedy_decisions d where d.claim_id = c.id),
                   (select d.reason from claims.remedy_decisions d where d.claim_id = c.id and d.kind = 'Decided'),
                   (select string_agg(t.step, ',' order by t.seq) from audit.decision_trail_entries t where t.claim_id = c.id),
                   (select count(*) from claims.repairs rp where rp.claim_id = c.id)
                 + (select count(*) from claims.remedy_completions rc where rc.claim_id = c.id)
            from claims.claims c
            join claims.claim_remedies r on r.claim_id = c.id
            join policy.policy_versions v on v.id = r.policy_version_id
            join policy.warranty_policies p on p.id = v.policy_id
            where c.reference like 'RMDYR11%'
            order by c.reference
            """,
            warranty);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            reader.GetString(7).ShouldStartWith("Two repair attempts did not fix");
            reader.GetInt64(9).ShouldBe(0);
            rows.Add(string.Join(" | ", Enumerable.Range(0, 9).Where(i => i != 7)
                .Select(i => Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        }

        rows.ShouldBe(
        [
            "RMDYR11A01 | Approved | AUR-WP v2 | RepairFailed | InProgress | Replacement | Evaluated:repairFailed:-:2,Decided:repairFailed:Reviewer:2 | ClaimSubmitted,AutoApproved,RemedyRecommended,RemedyDecided",
            "RMDYR11B01 | Approved | BOR-WP v1 | RepairFailed | InProgress | Refund | Evaluated:repairFailed:-:2,Decided:repairFailed:Reviewer:2 | ClaimSubmitted,AutoApproved,RemedyRecommended,RemedyDecided",
        ]);
    }

    [Fact]
    public async Task The_app_role_logs_in_with_the_configured_password_and_sees_no_tenant_rows_without_a_tenant()
    {
        (await RunMigrationServiceAsync()).ShouldBe(0);

        var app = new NpgsqlConnectionStringBuilder(Owner("warranty")) { Username = "warranty_app", Password = AppRolePassword };
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        (await ScalarAsync(connection, "select count(*) from tenancy.tenant_channels")).ShouldBe(2);
        (await ScalarAsync(connection, "select count(*) from catalog.products")).ShouldBe(0);
        var blobs = new BlobServiceClient(_azurite.GetConnectionString()).GetBlobContainerClient("knowledge-sources");
        var names = new List<string>();
        await foreach (var blob in blobs.GetBlobsAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            names.Add(blob.Name);
        }

        names.Count.ShouldBe(7);
        names.ShouldContain("tenant-aurora/policies/AUR-WP-v1.md");
    }

    private Task<int> RunMigrationServiceAsync()
        => MigrationRunner.RunAsync(
            Owner("warranty"), Owner("knowledge"), _azurite.GetConnectionString(), AppRolePassword, TestContext.Current.CancellationToken);

    private async Task<Dictionary<string, long>> CountsAsync()
    {
        string[] tables =
        [
            "tenancy.tenants", "tenancy.tenant_channels", "tenancy.tenant_settings", "catalog.products", "catalog.product_serials",
            "crm.customers", "integration.service_centers", "claims.claims", "audit.decision_trail_entries",
            "claims.claim_remedies", "claims.remedy_decisions", "claims.repairs", "claims.repair_handovers", "audit.repair_events",
            "policy.warranty_policies", "policy.policy_versions",
        ];
        await using var connection = await OpenAsync("warranty");
        var counts = new Dictionary<string, long>();
        foreach (var table in tables)
        {
            counts[table] = await ScalarAsync(connection, $"select count(*) from {table}");
        }

        return counts;
    }

    private async Task<Dictionary<string, long>> KnowledgeAsync()
    {
        await using var connection = await OpenAsync("knowledge");
        await using var command = new NpgsqlCommand(
            "select namespace, count(*) from knowledge.knowledge_documents d where exists (select 1 from knowledge.knowledge_chunks c where c.document_id = d.id) group by namespace",
            connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var result = new Dictionary<string, long>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            result[reader.GetString(0)] = reader.GetInt64(1);
        }

        return result;
    }

    private async Task<NpgsqlConnection> OpenAsync(string database)
    {
        var connection = new NpgsqlConnection(Owner(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private string Owner(string database) => new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database }.ConnectionString;
}
