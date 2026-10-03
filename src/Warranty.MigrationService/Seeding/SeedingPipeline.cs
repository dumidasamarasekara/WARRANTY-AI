using Warranty.Application.Abstractions;
using Warranty.Knowledge.Ingestion;

namespace Warranty.MigrationService.Seeding;

/// <summary>
/// The migration service's work, in order: migrate both databases and apply the owner SQL scripts,
/// create the knowledge partitions, seed every tenant, then index global knowledge (no tenant in
/// scope) and each tenant's policies (inside that tenant's scope). Each step runs in its own DI
/// scope, so no DbContext outlives the tenant it was used for.
/// </summary>
internal sealed class SeedingPipeline(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<SeedingPipeline> logger)
{
    /// <summary>Principal of every tenant scope the migration service opens.</summary>
    public const string Principal = "migration-service";

    public async Task RunAsync(CancellationToken ct)
    {
        var root = ResolveSeedPath(configuration[MigrationServiceExtensions.SeedPathKey]);
        var seed = SeedFiles.Load(root);
        logger.LogInformation("Seeding from {SeedPath}: tenants {Tenants}", root, string.Join(", ", seed.Tenants.Select(t => t.Tenant.Slug)));

        await InScopeAsync<DatabaseMigrator>(
            m => m.MigrateAsync([KnowledgeSourceValidator.GlobalNamespace, .. seed.Tenants.Select(t => t.Tenant.KnowledgeNamespace)], ct));

        foreach (var tenant in seed.Tenants)
        {
            await InScopeAsync<TenantSeeder>(s => s.SeedAsync(tenant, ct));
        }

        await InScopeAsync<KnowledgeSeeder>(k => k.IngestAsync(seed.GlobalKnowledge, ct));
        foreach (var tenant in seed.Tenants)
        {
            using var scope = TenantContextScope.Begin(tenant.Tenant.Id, tenant.Tenant.Slug, Principal);
            await InScopeAsync<KnowledgeSeeder>(k => k.IngestAsync(tenant.Knowledge, ct));
        }

        logger.LogInformation("Migration and seeding completed");
    }

    internal static string ResolveSeedPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "seed");
                if (Directory.Exists(Path.Combine(candidate, "tenants")))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException($"No seed folder found; set '{MigrationServiceExtensions.SeedPathKey}'.");
    }

    private async Task InScopeAsync<TStep>(Func<TStep, Task> work)
        where TStep : notnull
    {
        await using var scope = scopes.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<TStep>());
    }
}
