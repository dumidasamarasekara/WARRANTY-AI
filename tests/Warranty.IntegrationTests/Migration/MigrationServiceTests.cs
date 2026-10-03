extern alias migration;

using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using migration::Warranty.MigrationService.Seeding;
using Npgsql;
using Testcontainers.Azurite;
using Testcontainers.PostgreSql;

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
            ["catalog.products"] = 6,
            ["catalog.product_serials"] = 100,
            ["crm.customers"] = 9,
            ["integration.service_centers"] = 5,
            ["claims.claims"] = 2,
            ["audit.decision_trail_entries"] = 4,
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

    private async Task<int> RunMigrationServiceAsync()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:warranty"] = Owner("warranty"),
            ["ConnectionStrings:knowledge"] = Owner("knowledge"),
            ["ConnectionStrings:blobs"] = _azurite.GetConnectionString(),
            ["Database:AppRolePassword"] = AppRolePassword,
            ["AiGateway:Mode"] = "live",
            ["AiGateway:Routes:embedding:Provider"] = "hash",
            ["AiGateway:Routes:embedding:Model"] = "hash-embedding",
            ["AiGateway:Routes:embedding:Dimensions"] = "768",
            [MigrationServiceExtensions.SeedPathKey] = SeedRoot(),
        });
        builder.AddMigrationService();

        using var host = builder.Build();
        Environment.ExitCode = -1;
        await host.RunAsync(TestContext.Current.CancellationToken);
        return Environment.ExitCode;
    }

    private async Task<Dictionary<string, long>> CountsAsync()
    {
        string[] tables =
        [
            "tenancy.tenants", "tenancy.tenant_channels", "tenancy.tenant_settings", "catalog.products", "catalog.product_serials",
            "crm.customers", "integration.service_centers", "claims.claims", "audit.decision_trail_entries",
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

    private static string SeedRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Warranty.slnx")))
            {
                return Path.Combine(dir.FullName, "seed");
            }
        }

        throw new InvalidOperationException("Repository root (Warranty.slnx) not found.");
    }
}
