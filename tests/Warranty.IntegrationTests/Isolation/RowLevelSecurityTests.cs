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

    [Fact]
    public async Task Every_tenant_owned_table_shows_only_the_current_tenants_rows()
    {
        var tables = await TenantOwnedTablesAsync();
        tables.ShouldContain("claims.claims");
        tables.ShouldContain("claims.claim_jobs");
        tables.ShouldContain("audit.decision_trail_entries");
        tables.ShouldContain("audit.security_events");
        tables.ShouldNotContain("tenancy.tenant_channels");

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
        var claimId = await ScalarAsync<Guid>(owner, "select id from claims.claims where tenant_id = @aurora order by serial_number limit 1");
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
