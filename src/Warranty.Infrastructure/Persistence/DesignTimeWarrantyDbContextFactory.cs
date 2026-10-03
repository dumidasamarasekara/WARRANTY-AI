using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Warranty.Application.Abstractions;

namespace Warranty.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations</c> build the model without the API host. Never used at runtime: the
/// connection string is a placeholder and there is no tenant (migrations do not query).
/// </summary>
internal sealed class DesignTimeWarrantyDbContextFactory : IDesignTimeDbContextFactory<WarrantyDbContext>
{
    public WarrantyDbContext CreateDbContext(string[] args)
        => new(
            new DbContextOptionsBuilder<WarrantyDbContext>()
                .UseNpgsql("Host=localhost;Database=warranty;Username=design_time")
                .Options,
            new NoTenant());

    private sealed class NoTenant : ITenantContext
    {
        public bool IsResolved => false;

        public Guid TenantId => throw new InvalidOperationException("Design time has no tenant.");

        public string TenantSlug => throw new InvalidOperationException("Design time has no tenant.");

        public string KnowledgeNamespace => throw new InvalidOperationException("Design time has no tenant.");

        public string PrincipalId => "design-time";

        public string PrincipalName => "design-time";

        public IReadOnlySet<string> Roles { get; } = new HashSet<string>();

        public string CorrelationId => "design-time";

        public bool IsSystem => false;
    }
}
