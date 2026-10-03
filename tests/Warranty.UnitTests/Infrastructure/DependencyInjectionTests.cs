using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Jobs;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Abstractions.Storage;
using Warranty.Infrastructure;
using Warranty.Infrastructure.Audit;
using Warranty.Infrastructure.Persistence;
using Warranty.Infrastructure.Persistence.Knowledge;

namespace Warranty.UnitTests.Infrastructure;

public sealed class DependencyInjectionTests
{
    private const string OwnerConnection = "Host=db;Port=5432;Database=warranty;Username=postgres;Password=owner";

    private static IConfiguration Configuration(string? password = "app-secret", string? warranty = OwnerConnection)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:warranty"] = warranty,
            ["ConnectionStrings:knowledge"] = OwnerConnection.Replace("warranty", "knowledge", StringComparison.Ordinal),
            [DependencyInjection.AppRolePasswordKey] = password,
            ["ConnectionStrings:blobs"] = "UseDevelopmentStorage=true",
        }).Build();

    [Fact]
    public void The_app_always_connects_as_warranty_app_whatever_user_the_connection_string_names()
    {
        var builder = new NpgsqlConnectionStringBuilder(DependencyInjection.AppConnectionString(Configuration(), "warranty"));

        builder.Username.ShouldBe("warranty_app");
        builder.Password.ShouldBe("app-secret");
        builder.Host.ShouldBe("db");
        builder.Database.ShouldBe("warranty");
    }

    [Fact]
    public void Missing_app_role_password_or_connection_string_fails_at_registration()
    {
        Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddWarrantyInfrastructure(Configuration(password: null)))
            .Message.ShouldContain(DependencyInjection.AppRolePasswordKey);
        Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddWarrantyInfrastructure(Configuration(warranty: null)))
            .Message.ShouldContain("'warranty'");
    }

    [Fact]
    public void Repositories_and_both_contexts_resolve_per_scope_with_the_tenant_session_interceptor()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new FakeTenantContext(Guid.NewGuid()));
        services.AddLogging();
        services.AddWarrantyInfrastructure(Configuration());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        foreach (var port in new[]
                 {
                     typeof(IUnitOfWork), typeof(ITenantRepository), typeof(ICatalogRepository), typeof(IPolicyRepository),
                     typeof(IClaimRepository), typeof(IAdjudicationRepository), typeof(IReviewRepository), typeof(IAiOpsRepository),
                     typeof(IDocumentStore), typeof(IJobQueue), typeof(IDecisionTrailWriter), typeof(ISecurityEventWriter),
                     typeof(HashChainVerifier),
                 })
        {
            scope.ServiceProvider.GetRequiredService(port).ShouldNotBeNull(port.Name);
        }

        var interceptor = scope.ServiceProvider.GetRequiredService<TenantSessionInterceptor>();
        foreach (var context in new DbContext[] { scope.ServiceProvider.GetRequiredService<WarrantyDbContext>(), scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>() })
        {
            context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!.Interceptors!.ShouldContain(interceptor);
            new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString()).Username.ShouldBe("warranty_app");
        }
    }
}
