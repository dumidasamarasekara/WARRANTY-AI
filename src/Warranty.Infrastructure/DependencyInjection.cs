using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Jobs;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Abstractions.Storage;
using Warranty.Infrastructure.Audit;
using Warranty.Infrastructure.Jobs;
using Warranty.Infrastructure.Persistence;
using Warranty.Infrastructure.Persistence.Knowledge;
using Warranty.Infrastructure.Persistence.Repositories;
using Warranty.Infrastructure.Storage;

namespace Warranty.Infrastructure;

/// <summary>The PostgreSQL login the DbContexts connect with.</summary>
public enum DatabaseLogin
{
    /// <summary><see cref="DependencyInjection.AppRole"/>, subject to row-level security (API and worker).</summary>
    App,

    /// <summary>
    /// The login of the configured connection strings, i.e. the database owner. Only the migration
    /// service connects this way, to migrate, seed and index knowledge (research R8).
    /// </summary>
    Owner,
}

public static class DependencyInjection
{
    /// <summary>Connection string names (Aspire database resources).</summary>
    public const string WarrantyDatabase = "warranty";

    public const string KnowledgeDatabase = "knowledge";

    /// <summary>Connection string name of the blob service (Azurite under Aspire).</summary>
    public const string BlobsConnection = "blobs";

    /// <summary>The non-owner role the API and worker connect as; row-level security applies to it (research R8).</summary>
    public const string AppRole = "warranty_app";

    /// <summary>Configuration key for the <see cref="AppRole"/> password (set by the AppHost, applied by the migration service).</summary>
    public const string AppRolePasswordKey = "Database:AppRolePassword";

    /// <summary>
    /// Registers both DbContexts — connecting as <see cref="AppRole"/>, whatever user the configured
    /// connection strings name, unless <paramref name="login"/> is <see cref="DatabaseLogin.Owner"/> —
    /// with the <see cref="TenantSessionInterceptor"/>, plus the repositories, unit of work, document
    /// store, job queue and audit writers. Requires a scoped <c>ITenantContext</c> from the host.
    /// </summary>
    public static IServiceCollection AddWarrantyInfrastructure(
        this IServiceCollection services, IConfiguration configuration, DatabaseLogin login = DatabaseLogin.App)
    {
        var warranty = login == DatabaseLogin.Owner ? OwnerConnectionString(configuration, WarrantyDatabase) : AppConnectionString(configuration, WarrantyDatabase);
        var knowledge = login == DatabaseLogin.Owner ? OwnerConnectionString(configuration, KnowledgeDatabase) : AppConnectionString(configuration, KnowledgeDatabase);

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
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IIntegrationRepository, IntegrationRepository>();
        services.AddScoped<IKnowledgeStore, KnowledgeStore>();

        // A host may register its own BlobServiceClient (e.g. the Aspire client integration); otherwise
        // it is built from the "blobs" connection string on first use.
        services.TryAddSingleton(_ => new BlobServiceClient(
            configuration.GetConnectionString(BlobsConnection)
            ?? throw new InvalidOperationException($"Connection string '{BlobsConnection}' is not configured.")));
        services.AddScoped<IDocumentStore, BlobDocumentStore>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IJobQueue, PostgresJobQueue>();

        services.AddScoped<IDecisionTrailWriter, DecisionTrailWriter>();
        services.AddScoped<HashChainVerifier>();
        services.AddScoped<ISecurityEventWriter, SecurityEventWriter>();
        services.TryAddSingleton<IRequestSourceAccessor, NoRequestSource>();
        return services;
    }

    /// <summary>The named connection string with its credentials replaced by <see cref="AppRole"/>'s.</summary>
    internal static string AppConnectionString(IConfiguration configuration, string name)
    {
        var connectionString = OwnerConnectionString(configuration, name);
        var password = configuration[AppRolePasswordKey];
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException($"'{AppRolePasswordKey}' is not configured; the app connects only as {AppRole}.");
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { Username = AppRole, Password = password }.ConnectionString;
    }

    /// <summary>The named connection string as configured (the owner login under Aspire).</summary>
    internal static string OwnerConnectionString(IConfiguration configuration, string name)
    {
        var connectionString = configuration.GetConnectionString(name);
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException($"Connection string '{name}' is not configured.")
            : connectionString;
    }
}
