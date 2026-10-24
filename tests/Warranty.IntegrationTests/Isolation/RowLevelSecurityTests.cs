using System.Data;
using Npgsql;
using Warranty.IntegrationTests.Infrastructure;

namespace Warranty.IntegrationTests.Isolation;

/// <summary>
/// Row-level security as the API's login sees it: raw SQL as <c>warranty_app</c> with the session
/// variables TenantSessionInterceptor sets (research R8, R12, R30; FR-041a).
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class RowLevelSecurityTests(WarrantyAppFixture fixture)
{
    private static readonly Guid Aurora = TestStaffUsers.AuroraTenantId;
    private static readonly Guid Borealis = TestStaffUsers.BorealisTenantId;

    /// <summary>Tables the seed fills for both tenants, so the isolation checks are not vacuous.</summary>
    private static readonly string[] SeededForBothTenants =
    [
        "tenancy.tenant_settings", "catalog.products", "catalog.product_serials", "crm.customers",
        "integration.service_centers", "policy.warranty_policies", "policy.policy_versions",
    ];

    /// <summary>
    /// Tables of feature 002 (AddRemediesAndRepairs). They live in the existing claims and audit schemas, so
    /// 001_roles_and_rls.sql isolates them (and makes audit.repair_events append-only) without new SQL.
    /// </summary>
    private static readonly string[] RemedyAndRepairTables =
    [
        "claims.claim_remedies", "claims.remedy_decisions", "claims.remedy_completions", "claims.repairs",
        "claims.repair_handovers", "claims.repair_attempts", "audit.repair_events",
    ];

    /// <summary>
    /// Tables of feature 003 (AddReplacements), likewise in existing schemas: 001_roles_and_rls.sql isolates them and
    /// makes audit.replacement_events append-only without new SQL (T309).
    /// </summary>
    private static readonly string[] ReplacementTables =
    [
        "claims.replacements", "claims.replacement_locations", "claims.faulty_product_returns",
        "claims.replacement_model_proposals", "claims.replacement_units", "audit.replacement_events",
        "catalog.replacement_serials",
    ];

    [Fact]
    public async Task Every_tenant_owned_table_shows_only_the_current_tenants_rows()
    {
        var tables = await TenantOwnedTablesAsync();
        tables.ShouldContain("claims.claims");
        tables.ShouldContain("claims.claim_jobs");
        tables.ShouldContain("audit.decision_trail_entries");
        tables.ShouldContain("audit.security_events");
        tables.ShouldNotContain("tenancy.tenant_channels");
        RemedyAndRepairTables.Except(tables).ShouldBeEmpty("remedy and repair tables without the tenant policy");
        ReplacementTables.Except(tables).ShouldBeEmpty("replacement tables without the tenant policy");

        await using var owner = await OpenOwnerAsync();
        foreach (var table in SeededForBothTenants)
        {
            (await ScalarAsync<long>(owner, $"select count(*) from {table} where tenant_id = @borealis")).ShouldBeGreaterThan(0, table);
        }

        await using var app = await OpenAppAsync(tenant: Aurora);
        var leaks = new List<string>();
        foreach (var table in tables)
        {
            var foreign = await ScalarAsync<long>(app, $"select count(*) from {table} where tenant_id is distinct from @aurora");
            if (foreign > 0)
            {
                leaks.Add($"{table}: {foreign} rows of other tenants");
            }
        }

        leaks.ShouldBeEmpty();
        (await ScalarAsync<long>(app, "select count(*) from catalog.product_serials")).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Without_a_tenant_no_tenant_owned_row_is_visible()
    {
        await using var app = await OpenAppAsync();
        foreach (var table in await TenantOwnedTablesAsync())
        {
            (await ScalarAsync<long>(app, $"select count(*) from {table}")).ShouldBe(0, table);
        }
    }

    [Fact]
    public async Task Writing_a_row_of_another_tenant_is_refused_in_every_table()
    {
        await using var app = await OpenAppAsync(tenant: Aurora);
        await using var transaction = await app.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var tested = new List<string>();
        foreach (var table in await TenantOwnedTablesAsync())
        {
            if (await ScalarAsync<long>(app, $"select count(*) from {table}") == 0)
            {
                continue;
            }

            // Copy one visible Aurora row, re-keyed to Borealis; the WITH CHECK policy must refuse it.
            var columns = await InsertableColumnsAsync(table);
            var values = columns.Select(c => c.Name switch
            {
                "tenant_id" => "@borealis",
                "id" when c.Type == "uuid" => "gen_random_uuid()",
                _ => c.Name,
            });
            var insert = $"insert into {table} ({string.Join(", ", columns.Select(c => c.Name))}) " +
                         $"select {string.Join(", ", values)} from {table} limit 1";

            await transaction.SaveAsync("probe", TestContext.Current.CancellationToken);
            (await SqlStateOfAsync(app, insert)).ShouldBe(PostgresErrorCodes.InsufficientPrivilege, table);
            await transaction.RollbackAsync("probe", TestContext.Current.CancellationToken);
            tested.Add(table);
        }

        tested.ShouldContain("catalog.products");
        tested.ShouldContain("claims.claims");
        tested.ShouldContain("audit.decision_trail_entries");
    }

    [Fact]
    public async Task The_decision_trail_cannot_be_updated_or_deleted_by_the_app_or_the_owner()
    {
        await using var app = await OpenAppAsync(tenant: Aurora);
        (await ScalarAsync<long>(app, "select count(*) from audit.decision_trail_entries")).ShouldBeGreaterThan(0);

        (await SqlStateInRolledBackTransactionAsync(app, "update audit.decision_trail_entries set summary = summary"))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await SqlStateInRolledBackTransactionAsync(app, "delete from audit.decision_trail_entries"))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // The append-only trigger stops even the owner.
        await using var owner = await OpenOwnerAsync();
        (await SqlStateInRolledBackTransactionAsync(owner, "update audit.decision_trail_entries set summary = summary"))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await SqlStateInRolledBackTransactionAsync(owner, "delete from audit.decision_trail_entries"))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Every_audit_table_including_repair_events_has_the_append_only_trigger_and_no_app_update_or_delete()
    {
        await using var owner = await OpenOwnerAsync();
        (await ScalarAsync<long>(
                owner,
                """
                select count(*) from information_schema.tables t
                where t.table_schema = 'audit' and t.table_type = 'BASE TABLE'
                  and not exists (
                      select 1 from pg_trigger g
                      where g.tgrelid = format('%I.%I', t.table_schema, t.table_name)::regclass
                        and g.tgname = 'append_only' and not g.tgisinternal)
                """))
            .ShouldBe(0, "audit tables without the append_only trigger");
        (await ScalarAsync<long>(
                owner,
                "select count(*) from pg_trigger where tgrelid = 'audit.repair_events'::regclass and tgname = 'append_only'"))
            .ShouldBe(1);
        (await ScalarAsync<bool>(
                owner,
                "select has_table_privilege('warranty_app', 'audit.repair_events', 'UPDATE') or " +
                "has_table_privilege('warranty_app', 'audit.repair_events', 'DELETE')"))
            .ShouldBeFalse();
        (await ScalarAsync<bool>(owner, "select has_table_privilege('warranty_app', 'audit.repair_events', 'INSERT')"))
            .ShouldBeTrue();
    }

    /// <summary>
    /// Feature 002 tables are empty until remedies are seeded, so the generic checks above would pass
    /// vacuously. This probe writes a row of every new table for both tenants (see <see cref="ShouldIsolateProbesAsync"/>).
    /// </summary>
    [Fact]
    [Trait("Feature", "002")]
    public Task Rows_of_every_remedy_and_repair_table_are_visible_and_writable_only_for_their_own_tenant()
        => ShouldIsolateProbesAsync(RemedyAndRepairProbes, RemedyAndRepairTables);

    /// <summary>Feature 003 tables are empty until replacements exist, so they get the same raw-SQL probe as 002.</summary>
    [Fact]
    [Trait("Feature", "003")]
    public Task Rows_of_every_replacement_table_are_visible_and_writable_only_for_their_own_tenant()
        => ShouldIsolateProbesAsync(ReplacementProbes, ReplacementTables);

    [Fact]
    [Trait("Feature", "003")]
    public async Task A_replacement_event_cannot_be_updated_or_deleted_by_the_app_or_the_owner()
    {
        await using var owner = await OpenOwnerAsync();
        (await ScalarAsync<long>(
                owner,
                "select count(*) from pg_trigger where tgrelid = 'audit.replacement_events'::regclass and tgname = 'append_only'"))
            .ShouldBe(1);
        (await ScalarAsync<bool>(
                owner,
                "select has_table_privilege('warranty_app', 'audit.replacement_events', 'UPDATE') or " +
                "has_table_privilege('warranty_app', 'audit.replacement_events', 'DELETE')"))
            .ShouldBeFalse();

        await using var transaction = await owner.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var probe = await CreateProbeClaimAsync(owner, Aurora);
        var events = ReplacementProbes.Single(t => t.Name == "audit.replacement_events");
        foreach (var table in ReplacementProbes.Where(t => t.Name is "claims.replacements" or "audit.replacement_events"))
        {
            await ExecuteAsync(owner, table.Insert, probe.Parameters(table));
        }

        var scope = probe.Parameters(events);
        (await ScalarAsync<long>(owner, events.Count, scope)).ShouldBe(1);

        // The append-only trigger stops even the owner.
        (await SqlStateInSavepointAsync(owner, transaction, "update audit.replacement_events set note = 'changed' where replacement_id = @replacement", scope))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await SqlStateInSavepointAsync(owner, transaction, "delete from audit.replacement_events where replacement_id = @replacement", scope))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        await ExecuteAsync(owner, "set local role warranty_app");
        await SetLocalTenantAsync(owner, Aurora);
        (await ScalarAsync<long>(owner, events.Count, scope)).ShouldBe(1, "the app sees its tenant's event");
        (await SqlStateInSavepointAsync(owner, transaction, "update audit.replacement_events set note = 'changed' where replacement_id = @replacement", scope))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await SqlStateInSavepointAsync(owner, transaction, "delete from audit.replacement_events where replacement_id = @replacement", scope))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Writes a row of every probed table for both tenants inside one rolled-back transaction: as <c>warranty_app</c>
    /// with tenant Aurora it inserts Aurora's rows (positive control: the grants and the WITH CHECK policy let them
    /// through), sees no Borealis row, and is refused a Borealis row in every table; with tenant Borealis it sees the
    /// mirror image.
    /// </summary>
    private async Task ShouldIsolateProbesAsync(ProbeTable[] probes, string[] tables)
    {
        await using var owner = await OpenOwnerAsync();
        await using var transaction = await owner.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var aurora = await CreateProbeClaimAsync(owner, Aurora);
        var borealis = await CreateProbeClaimAsync(owner, Borealis);
        foreach (var table in probes)
        {
            await ExecuteAsync(owner, table.Insert, borealis.Parameters(table));
        }

        await ExecuteAsync(owner, "set local role warranty_app");
        await SetLocalTenantAsync(owner, Aurora);
        foreach (var table in probes)
        {
            (await SqlStateInSavepointAsync(owner, transaction, table.Insert, aurora.Parameters(table)))
                .ShouldBeNull($"{table.Name}: the app may write a row of its own tenant");
        }

        probes.Select(t => t.Name).ShouldBe(tables, ignoreOrder: true);
        foreach (var table in probes)
        {
            (await ScalarAsync<long>(owner, table.Count, aurora.Parameters(table))).ShouldBe(1, $"{table.Name}: own row");
            (await ScalarAsync<long>(owner, table.Count, borealis.Parameters(table))).ShouldBe(0, $"{table.Name}: Borealis row");
            (await ScalarAsync<long>(owner, $"select count(*) from {table.Name} where tenant_id is distinct from @aurora"))
                .ShouldBe(0, $"{table.Name}: rows of other tenants");
            var fresh = borealis.Parameters(table, newRepair: Guid.CreateVersion7(), newReplacement: Guid.CreateVersion7());
            (await SqlStateInSavepointAsync(owner, transaction, table.Insert, fresh))
                .ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{table.Name}: writing a Borealis row");
        }

        await SetLocalTenantAsync(owner, Borealis);
        foreach (var table in probes)
        {
            (await ScalarAsync<long>(owner, table.Count, borealis.Parameters(table))).ShouldBe(1, $"{table.Name}: own row");
            (await ScalarAsync<long>(owner, table.Count, aurora.Parameters(table))).ShouldBe(0, $"{table.Name}: Aurora row");
        }

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    [Trait("Feature", "002")]
    public async Task A_repair_event_cannot_be_updated_or_deleted_by_the_app_or_the_owner()
    {
        await using var owner = await OpenOwnerAsync();
        await using var transaction = await owner.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var probe = await CreateProbeClaimAsync(owner, Aurora);
        var events = RemedyAndRepairProbes.Single(t => t.Name == "audit.repair_events");
        foreach (var table in RemedyAndRepairProbes.Where(t => t.Name is "claims.repairs" or "audit.repair_events"))
        {
            await ExecuteAsync(owner, table.Insert, probe.Parameters(table, newRepair: probe.RepairId));
        }

        var scope = probe.Parameters(events);
        (await ScalarAsync<long>(owner, events.Count, scope)).ShouldBe(1);

        // The append-only trigger stops even the owner.
        (await SqlStateInSavepointAsync(owner, transaction, "update audit.repair_events set note = 'changed' where repair_id = @repair", scope))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await SqlStateInSavepointAsync(owner, transaction, "delete from audit.repair_events where repair_id = @repair", scope))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        await ExecuteAsync(owner, "set local role warranty_app");
        await SetLocalTenantAsync(owner, Aurora);
        (await ScalarAsync<long>(owner, events.Count, scope)).ShouldBe(1, "the app sees its tenant's event");
        (await SqlStateInSavepointAsync(owner, transaction, "update audit.repair_events set note = 'changed' where repair_id = @repair", scope))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await SqlStateInSavepointAsync(owner, transaction, "delete from audit.repair_events where repair_id = @repair", scope))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Knowledge_of_another_tenants_namespace_is_invisible()
    {
        await using var owner = await OpenOwnerAsync("knowledge");
        (await ScalarAsync<long>(owner, "select count(*) from knowledge.knowledge_chunks where namespace = 'tenant-borealis'"))
            .ShouldBeGreaterThan(0);

        await using var aurora = await OpenAppAsync("knowledge", kbNamespace: "tenant-aurora");
        (await NamespacesAsync(aurora, "knowledge.knowledge_chunks")).ShouldBe(["global", "tenant-aurora"]);
        (await NamespacesAsync(aurora, "knowledge.knowledge_documents")).ShouldBe(["global", "tenant-aurora"]);

        await using var none = await OpenAppAsync("knowledge");
        (await NamespacesAsync(none, "knowledge.knowledge_chunks")).ShouldBe(["global"]);
    }

    [Fact]
    public async Task Claim_jobs_of_another_tenant_are_hidden_and_the_dequeue_function_returns_only_the_job_header()
    {
        await using var owner = await OpenOwnerAsync();
        // A claim without a round-1 job: claims submitted by other tests already have one (unique per claim and round).
        var claimId = await ScalarAsync<Guid>(
            owner,
            "select c.id from claims.claims c where c.tenant_id = @aurora " +
            "and not exists (select 1 from claims.claim_jobs j where j.claim_id = c.id and j.round = 1) order by c.serial_number limit 1");
        var jobId = Guid.CreateVersion7();

        // Due tomorrow, so the running worker never leases it.
        await ExecuteAsync(
            owner,
            "insert into claims.claim_jobs (id, tenant_id, claim_id, round, status, attempts, available_at, correlation_id) " +
            "values (@job, @aurora, @claim, 1, 'Queued', 0, now() + interval '1 day', 'rls-test')",
            ("job", jobId), ("claim", claimId));
        try
        {
            await using (var borealis = await OpenAppAsync(tenant: Borealis))
            {
                (await ScalarAsync<long>(borealis, "select count(*) from claims.claim_jobs where id = @job", ("job", jobId))).ShouldBe(0);
                (await ScalarAsync<long>(borealis, "select count(*) from claims.claim_jobs where tenant_id = @aurora")).ShouldBe(0);
            }

            await using (var aurora = await OpenAppAsync(tenant: Aurora))
            {
                (await ScalarAsync<long>(aurora, "select count(*) from claims.claim_jobs where id = @job", ("job", jobId))).ShouldBe(1);
            }

            // Described, not executed: calling it would lease jobs of the running worker.
            await using var app = await OpenAppAsync();
            await using var command = new NpgsqlCommand("select * from claims.dequeue_claim_job('rls-test', 1)", app);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, TestContext.Current.CancellationToken);
            var columns = await reader.GetColumnSchemaAsync(TestContext.Current.CancellationToken);
            columns.Select(c => c.ColumnName).ShouldBe(["job_id", "tenant_id", "claim_id", "round", "correlation_id"]);
        }
        finally
        {
            await ExecuteAsync(owner, "delete from claims.claim_jobs where id = @job", ("job", jobId));
        }
    }

    [Fact]
    public async Task Operator_security_events_are_invisible_and_only_insertable_through_the_definer_function()
    {
        var actor = $"rls-test-{Guid.CreateVersion7():N}";
        await using var owner = await OpenOwnerAsync();
        await ExecuteAsync(
            owner, "select audit.record_operator_event('UNKNOWN_CHANNEL', 'rls-test-owner', 'owner.localhost', '{}'::jsonb)");

        await using var app = await OpenAppAsync(tenant: Aurora);
        (await ScalarAsync<long>(app, "select count(*) from audit.security_events where tenant_id is null")).ShouldBe(0);

        (await SqlStateInRolledBackTransactionAsync(
                app,
                "insert into audit.security_events (id, tenant_id, occurred_at, kind, actor, target, details) " +
                "values (gen_random_uuid(), null, now(), 'UNKNOWN_CHANNEL', 'rls-test', 'app.localhost', '{}'::jsonb)"))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        await ExecuteAsync(
            app, "select audit.record_operator_event('UNKNOWN_CHANNEL', @actor, 'app.localhost', '{}'::jsonb)", ("actor", actor));
        (await ScalarAsync<long>(owner, "select count(*) from audit.security_events where actor = @actor and tenant_id is null", ("actor", actor)))
            .ShouldBe(1);
        (await ScalarAsync<long>(app, "select count(*) from audit.security_events where actor = @actor", ("actor", actor))).ShouldBe(0);

        (await SqlStateInRolledBackTransactionAsync(
                app, "select audit.record_operator_event('ACCESS_DENIED', 'rls-test', 'x', '{}'::jsonb)"))
            .ShouldBe(PostgresErrorCodes.InvalidParameterValue);

        (await SqlStateInRolledBackTransactionAsync(app, "update audit.security_events set actor = actor"))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await SqlStateInRolledBackTransactionAsync(app, "delete from audit.security_events"))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await SqlStateInRolledBackTransactionAsync(owner, "update audit.security_events set actor = actor"))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await SqlStateInRolledBackTransactionAsync(owner, "delete from audit.security_events"))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    /// <summary>
    /// One row of each feature-002 table, parameterised by tenant (<c>@tenant</c>), probe claim (<c>@claim</c>),
    /// policy version (<c>@pv</c>), repair centre (<c>@centre</c>) and repair (<c>@repair</c>; the repair row
    /// itself uses <c>@newRepair</c>), with <see cref="ProbeTable.Count"/> counting the probe's rows.
    /// </summary>
    private static readonly ProbeTable[] RemedyAndRepairProbes =
    [
        new("claims.claim_remedies", "claim_id = @claim",
            "insert into claims.claim_remedies (id, tenant_id, claim_id, policy_version_id, scenario, state, remedy, allowed_remedies, updated_at) " +
            "values (gen_random_uuid(), @tenant, @claim, @pv, 'CoveredDefect', 'InProgress', 'Repair', array['Repair'], now())"),
        new("claims.remedy_decisions", "claim_id = @claim",
            "insert into claims.remedy_decisions (id, tenant_id, claim_id, seq, kind, scenario, policy_version_id, table_row, allowed_remedies, " +
            "remedy, decided_by, actor, failed_attempts, created_at) values (gen_random_uuid(), @tenant, @claim, 1, 'Evaluated', " +
            "'CoveredDefect', @pv, 'coveredDefect', array['Repair'], 'Repair', 'Policy', 'rls-test-002', 0, now())"),
        new("claims.remedy_completions", "claim_id = @claim",
            "insert into claims.remedy_completions (id, tenant_id, claim_id, remedy, completed_on, recorded_by, recorded_at) " +
            "values (gen_random_uuid(), @tenant, @claim, 'Repair', current_date, 'rls-test-002', now())"),
        new("claims.repairs", "claim_id = @claim",
            "insert into claims.repairs (id, tenant_id, claim_id, status, failed_attempts, created_at, updated_at) " +
            "values (@newRepair, @tenant, @claim, 'AwaitingHandover', 0, now(), now())"),
        new("claims.repair_handovers", "repair_id = @repair",
            "insert into claims.repair_handovers (id, tenant_id, repair_id, service_center_id, assignment, is_active, recorded_by, recorded_at) " +
            "values (gen_random_uuid(), @tenant, @repair, @centre, 'Suggested', false, 'rls-test-002', now())"),
        new("claims.repair_attempts", "repair_id = @repair",
            "insert into claims.repair_attempts (id, tenant_id, repair_id, seq, service_center_id, result, started_at) " +
            "values (gen_random_uuid(), @tenant, @repair, 1, @centre, 'InProgress', now())"),
        new("audit.repair_events", "repair_id = @repair",
            "insert into audit.repair_events (tenant_id, repair_id, claim_id, seq, type, reported_at, recorded_at, source, actor, payload) " +
            "values (@tenant, @repair, @claim, 1, 'CentreChanged', now(), now(), 'Phone', 'rls-test-002', '{}'::jsonb)"),
    ];

    /// <summary>
    /// One row of each feature-003 table, parameterised like <see cref="RemedyAndRepairProbes"/> plus the claim's
    /// product (<c>@product</c>) and replacement (<c>@replacement</c>; the replacement row itself uses
    /// <c>@newReplacement</c>). The serial numbers are unique per tenant only, so both tenants may use the same.
    /// </summary>
    private static readonly ProbeTable[] ReplacementProbes =
    [
        new("claims.replacements", "claim_id = @claim",
            "insert into claims.replacements (id, tenant_id, claim_id, policy_version_id, product_rule, return_rule, minimum_cover_months, " +
            "status, return_state, created_at, updated_at) values (@newReplacement, @tenant, @claim, @pv, 'SameModel', 'AtCollection', 0, " +
            "'AwaitingUnit', 'NotYetReturned', now(), now())"),
        new("claims.replacement_locations", "replacement_id = @replacement",
            "insert into claims.replacement_locations (id, tenant_id, replacement_id, service_center_id, assignment, is_active, recorded_by, recorded_at) " +
            "values (gen_random_uuid(), @tenant, @replacement, @centre, 'Suggested', false, 'rls-test-003', now())"),
        new("claims.faulty_product_returns", "replacement_id = @replacement",
            "insert into claims.faulty_product_returns (id, tenant_id, replacement_id, returned_on, serial_matches, recorded_by, recorded_at) " +
            "values (gen_random_uuid(), @tenant, @replacement, current_date, true, 'rls-test-003', now())"),
        new("claims.replacement_model_proposals", "replacement_id = @replacement",
            "insert into claims.replacement_model_proposals (id, tenant_id, replacement_id, product_id, reason, state, proposed_by, proposed_at) " +
            "values (gen_random_uuid(), @tenant, @replacement, @product, 'RLS probe', 'Declined', 'rls-test-003', now())"),
        new("claims.replacement_units", "replacement_id = @replacement",
            "insert into claims.replacement_units (id, tenant_id, replacement_id, seq, service_center_id, product_id, serial_number, result, reserved_at) " +
            "values (gen_random_uuid(), @tenant, @replacement, 1, @centre, @product, 'RLS-003-UNIT', 'Released', now())"),
        new("audit.replacement_events", "replacement_id = @replacement",
            "insert into audit.replacement_events (tenant_id, replacement_id, claim_id, seq, type, reported_at, recorded_at, source, actor, payload) " +
            "values (@tenant, @replacement, @claim, 1, 'LocationSuggested', now(), now(), 'Phone', 'rls-test-003', '{}'::jsonb)"),
        new("catalog.replacement_serials", "claim_id = @claim",
            "insert into catalog.replacement_serials (id, tenant_id, serial_number, product_id, original_serial, claim_id, original_purchase_date, " +
            "handed_over_on, cover_end_date, created_at) values (gen_random_uuid(), @tenant, 'RLS-003-UNIT', @product, 'RLS-002-PROBE', @claim, " +
            "current_date - 30, current_date, current_date + 365, now())"),
    ];

    private sealed record ProbeTable(string Name, string Scope, string Insert)
    {
        public string Count => $"select count(*) from {Name} where {Scope}";
    }

    /// <summary>
    /// The parent rows a probe needs in one tenant: a new claim (with its product), a seeded policy version and centre,
    /// and the ids its repair and replacement rows use.
    /// </summary>
    private sealed record ProbeClaim(Guid TenantId, Guid ClaimId, Guid ProductId, Guid PolicyVersionId, Guid CentreId, Guid RepairId, Guid ReplacementId)
    {
        /// <summary>
        /// Parameters for <paramref name="table"/>; <paramref name="newRepair"/> and <paramref name="newReplacement"/> are the
        /// ids a repair or replacement insert uses (the probe's own ids by default).
        /// </summary>
        public (string Name, object Value)[] Parameters(ProbeTable table, Guid? newRepair = null, Guid? newReplacement = null)
        {
            var parameters = new List<(string, object)>();
            void Add(string name, object value)
            {
                if (table.Insert.Contains('@' + name, StringComparison.Ordinal) || table.Scope.Contains('@' + name, StringComparison.Ordinal))
                {
                    parameters.Add((name, value));
                }
            }

            Add("tenant", TenantId);
            Add("claim", ClaimId);
            Add("pv", PolicyVersionId);
            Add("centre", CentreId);
            Add("repair", RepairId);
            Add("newRepair", newRepair ?? RepairId);
            Add("product", ProductId);
            Add("replacement", ReplacementId);
            Add("newReplacement", newReplacement ?? ReplacementId);
            return [.. parameters];
        }
    }

    /// <summary>Creates (as the owner, inside the caller's transaction) an approved claim of the tenant to hang the probe rows on.</summary>
    private static async Task<ProbeClaim> CreateProbeClaimAsync(NpgsqlConnection owner, Guid tenant)
    {
        var claimId = Guid.CreateVersion7();
        var reference = "R" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant();
        await ExecuteAsync(
            owner,
            """
            insert into claims.claims (id, tenant_id, reference, channel, submitted_by, customer_id, product_model_code, product_id,
                serial_number, purchase_date, purchase_place, purchase_price, region, problem_description, claim_date, status,
                requested_items, current_round, auto_info_request_count, reviewer_info_requested, created_at, updated_at)
            select @claim, @tenant, @reference, 'Claimant', 'rls-test-002', c.id, p.model_code, p.id,
                'RLS-002-PROBE', current_date - 30, 'RLS probe', 1, 'NA', 'Row-level security probe for feature 002.', current_date,
                'Approved', '[]'::jsonb, 1, 0, false, now(), now()
            from crm.customers c cross join catalog.products p
            where c.tenant_id = @tenant and p.tenant_id = @tenant
            limit 1
            """,
            ("claim", claimId), ("tenant", tenant), ("reference", reference));

        var product = await ScalarAsync<Guid>(owner, "select product_id from claims.claims where id = @claim", ("claim", claimId));
        var policyVersion = await ScalarAsync<Guid>(owner, "select id from policy.policy_versions where tenant_id = @tenant limit 1", ("tenant", tenant));
        var centre = await ScalarAsync<Guid>(owner, "select id from integration.service_centers where tenant_id = @tenant limit 1", ("tenant", tenant));
        return new ProbeClaim(tenant, claimId, product, policyVersion, centre, Guid.CreateVersion7(), Guid.CreateVersion7());
    }

    private static Task SetLocalTenantAsync(NpgsqlConnection connection, Guid tenant)
        => ExecuteAsync(connection, "select set_config('app.tenant_id', @tenant, true)", ("tenant", tenant.ToString()));

    /// <summary>The SQLSTATE the statement fails with (null on success), keeping the surrounding transaction usable.</summary>
    private static async Task<string?> SqlStateInSavepointAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await transaction.SaveAsync("probe", TestContext.Current.CancellationToken);
        try
        {
            await ExecuteAsync(connection, sql, parameters);
            await transaction.ReleaseAsync("probe", TestContext.Current.CancellationToken);
            return null;
        }
        catch (PostgresException exception)
        {
            await transaction.RollbackAsync("probe", TestContext.Current.CancellationToken);
            return exception.SqlState;
        }
    }

    /// <summary>Every table with a <c>tenant_id</c> column that the tenant policy covers.</summary>
    private async Task<List<string>> TenantOwnedTablesAsync()
    {
        await using var owner = await OpenOwnerAsync();
        await using var command = new NpgsqlCommand(
            """
            select c.relnamespace::regnamespace::text || '.' || c.relname
            from pg_class c
            join pg_policy p on p.polrelid = c.oid and p.polname = 'tenant_isolation'
            where c.relrowsecurity and c.relforcerowsecurity
            order by 1
            """,
            owner);
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        // Every table with a tenant_id column (except the channel registry) must be among them.
        await using var missing = new NpgsqlCommand(
            """
            select count(*) from information_schema.columns c
            join information_schema.tables t on t.table_schema = c.table_schema and t.table_name = c.table_name
            where c.column_name = 'tenant_id' and t.table_type = 'BASE TABLE'
              and c.table_schema not in ('pg_catalog', 'information_schema', 'public')
              and (c.table_schema, c.table_name) <> ('tenancy', 'tenant_channels')
              and c.table_schema || '.' || c.table_name <> all(@tables)
            """,
            owner);
        missing.Parameters.AddWithValue("tables", tables.ToArray());
        await reader.DisposeAsync();
        ((long)(await missing.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(0, "tables with tenant_id but no tenant policy");
        return tables;
    }

    private async Task<List<(string Name, string Type)>> InsertableColumnsAsync(string table)
    {
        var (schema, name) = (table.Split('.')[0], table.Split('.')[1]);
        await using var owner = await OpenOwnerAsync();
        await using var command = new NpgsqlCommand(
            """
            select column_name, udt_name from information_schema.columns
            where table_schema = @schema and table_name = @name
              and is_generated = 'NEVER' and coalesce(identity_generation, '') <> 'ALWAYS'
            order by ordinal_position
            """,
            owner);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("name", name);
        var columns = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns.Add((reader.GetString(0), reader.GetString(1)));
        }

        return columns;
    }

    private static async Task<List<string>> NamespacesAsync(NpgsqlConnection connection, string table)
    {
        await using var command = new NpgsqlCommand($"select distinct namespace from {table} order by namespace", connection);
        var namespaces = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            namespaces.Add(reader.GetString(0));
        }

        return namespaces;
    }

    private Task<NpgsqlConnection> OpenOwnerAsync(string database = "warranty")
        => OpenAsync(fixture.OwnerConnectionString(database));

    /// <summary>A <c>warranty_app</c> session with the variables TenantSessionInterceptor would set.</summary>
    private async Task<NpgsqlConnection> OpenAppAsync(string database = "warranty", Guid? tenant = null, string? kbNamespace = null)
    {
        var connection = await OpenAsync(fixture.AppConnectionString(database));
        await ExecuteAsync(
            connection,
            "select set_config('app.tenant_id', @tenant, false), set_config('app.kb_namespace', @namespace, false)",
            ("tenant", tenant?.ToString() ?? string.Empty),
            ("namespace", kbNamespace ?? string.Empty));
        return connection;
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        // No pooling: session variables must not leak between tests.
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("aurora", Aurora);
        command.Parameters.AddWithValue("borealis", Borealis);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>The SQLSTATE the statement fails with, or null when it succeeds.</summary>
    private static async Task<string?> SqlStateOfAsync(NpgsqlConnection connection, string sql)
    {
        try
        {
            await ExecuteAsync(connection, sql);
            return null;
        }
        catch (PostgresException exception)
        {
            return exception.SqlState;
        }
    }

    /// <summary>Like <see cref="SqlStateOfAsync"/>, but nothing the statement does is kept.</summary>
    private static async Task<string?> SqlStateInRolledBackTransactionAsync(NpgsqlConnection connection, string sql)
    {
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var state = await SqlStateOfAsync(connection, sql);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        return state;
    }
}
