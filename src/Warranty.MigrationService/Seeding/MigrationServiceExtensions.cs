using Warranty.AI.Gateway;
using Warranty.Application.Abstractions;
using Warranty.Infrastructure;
using Warranty.Knowledge;

namespace Warranty.MigrationService.Seeding;

public static class MigrationServiceExtensions
{
    /// <summary>Configuration key of the seed folder; when unset, the nearest <c>seed</c> folder above the app or working directory.</summary>
    public const string SeedPathKey = "Seed:Path";

    /// <summary>
    /// Registers the one-shot migration and seeding pipeline. The migration service is the documented
    /// owner connection (research R8); tenant work still runs inside a <see cref="TenantContextScope"/>,
    /// so row-level security checks every tenant write.
    /// </summary>
    public static IHostApplicationBuilder AddMigrationService(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<ITenantContext, AmbientTenantContext>();
        builder.Services
            .AddWarrantyInfrastructure(builder.Configuration, DatabaseLogin.Owner)
            .AddWarrantyAiGateway(builder.Configuration)
            .AddWarrantyKnowledge();

        builder.Services.AddScoped<DatabaseMigrator>();
        builder.Services.AddScoped<TenantSeeder>();
        builder.Services.AddScoped<KnowledgeSeeder>();
        builder.Services.AddSingleton<SeedingPipeline>();
        builder.Services.AddHostedService<MigrationWorker>();
        return builder;
    }
}
