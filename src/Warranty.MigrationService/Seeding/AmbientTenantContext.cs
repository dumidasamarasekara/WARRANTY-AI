using Warranty.Application.Abstractions;

namespace Warranty.MigrationService.Seeding;

/// <summary>
/// The migration service's <see cref="ITenantContext"/>: the innermost <see cref="TenantContextScope"/>
/// opened by a seeding step, or no tenant at all (platform work such as global knowledge).
/// </summary>
internal sealed class AmbientTenantContext : ITenantContext
{
    private readonly string _correlationId = Guid.CreateVersion7().ToString("N");

    public bool IsResolved => TenantContextScope.Current is not null;

    public Guid TenantId => Scope.TenantId;

    public string TenantSlug => Scope.TenantSlug;

    public string KnowledgeNamespace => Scope.KnowledgeNamespace;

    public string PrincipalId => TenantContextScope.Current?.PrincipalId ?? SeedingPipeline.Principal;

    public string PrincipalName => TenantContextScope.Current?.PrincipalName ?? SeedingPipeline.Principal;

    public IReadOnlySet<string> Roles => TenantContextScope.Current?.Roles ?? new HashSet<string>();

    public string CorrelationId => TenantContextScope.Current?.CorrelationId ?? _correlationId;

    public bool IsSystem => TenantContextScope.Current?.IsSystem ?? true;

    private static TenantContextScope Scope
        => TenantContextScope.Current ?? throw new InvalidOperationException("No tenant is in scope for this seeding step.");
}
