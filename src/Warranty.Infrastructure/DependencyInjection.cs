using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Infrastructure.Persistence;
using Warranty.Infrastructure.Persistence.Knowledge;
using Warranty.Infrastructure.Persistence.Repositories;

namespace Warranty.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Connection string names (Aspire database resources).</summary>
    public const string WarrantyDatabase = "warranty";

    public const string KnowledgeDatabase = "knowledge";

    /// <summary>The non-owner role the API and worker connect as; row-level security applies to it (research R8).</summary>
    public const string AppRole = "warranty_app";

    /// <summary>Configuration key for the <see cref="AppRole"/> password (set by the AppHost, applied by the migration service).</summary>
    public const string AppRolePasswordKey = "Database:AppRolePassword";

    /// <summary>
    /// Registers both DbContexts — connecting as <see cref="AppRole"/>, whatever user the configured
    /// connection strings name — with the <see cref="TenantSessionInterceptor"/>, plus the
    /// repositories and unit of work. Requires a scoped <c>ITenantContext</c> from the host.
    /// </summary>
    public static IServiceCollection AddWarrantyInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var warranty = AppConnectionString(configuration, WarrantyDatabase);
        var knowledge = AppConnectionString(configuration, KnowledgeDatabase);

        services.AddScoped<TenantSessionInterceptor>();
        services.AddDbContext<WarrantyDbContext>((sp, options) => options
            .UseNpgsql(warranty)
            .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>()));
        services.AddDbContext<KnowledgeDbContext>((sp, options) => options
            .UseNpgsql(knowledge, npgsql => npgsql.UseVector())
            .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>()));

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ICatalogRepository, CatalogRepository>();
        services.AddScoped<IPolicyRepository, PolicyRepository>();
        services.AddScoped<IClaimRepository, ClaimRepository>();
        services.AddScoped<IAdjudicationRepository, AdjudicationRepository>();
        services.AddScoped<IReviewRepository, ReviewRepository>();
        services.AddScoped<IAiOpsRepository, AiOpsRepository>();
        return services;
    }

    /// <summary>The named connection string with its credentials replaced by <see cref="AppRole"/>'s.</summary>
    internal static string AppConnectionString(IConfiguration configuration, string name)
    {
        var connectionString = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"Connection string '{name}' is not configured.");
        }

        var password = configuration[AppRolePasswordKey];
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException($"'{AppRolePasswordKey}' is not configured; the app connects only as {AppRole}.");
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { Username = AppRole, Password = password }.ConnectionString;
    }
}
