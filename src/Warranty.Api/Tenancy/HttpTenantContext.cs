using Warranty.Application.Abstractions;

namespace Warranty.Api.Tenancy;

/// <summary>
/// The scoped <see cref="ITenantContext"/> of the API host. In a request it holds what
/// <see cref="TenantResolutionMiddleware"/> resolved from trusted context before the endpoint runs; in
/// a DI scope opened by the claim job worker it reflects the ambient <see cref="TenantContextScope"/>.
/// The tenant of a request is set once and cannot be replaced.
/// </summary>
internal sealed class HttpTenantContext : ITenantContext
{
    private Resolution? _resolution;

    public bool IsResolved => Current is not null;

    public Guid TenantId => Required.TenantId;

    public string TenantSlug => Required.TenantSlug;

    public string KnowledgeNamespace => Required.KnowledgeNamespace;

    public string PrincipalId => Required.PrincipalId;

    public string PrincipalName => Required.PrincipalName;

    public IReadOnlySet<string> Roles => Required.Roles;

    public string CorrelationId => Required.CorrelationId;

    public bool IsSystem => Required.IsSystem;

    private ITenantContext? Current => (ITenantContext?)_resolution ?? TenantContextScope.Current;

    private ITenantContext Required => Current
        ?? throw new InvalidOperationException("No tenant has been resolved for the current request or job.");

    /// <summary>Sets the tenant and principal of the current request; called by <see cref="TenantResolutionMiddleware"/> only.</summary>
    public void Resolve(ResolvedTenant tenant, string principalId, string principalName, IEnumerable<string> roles, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        if (_resolution is not null)
        {
            throw new InvalidOperationException("The tenant of this request is already resolved.");
        }

        _resolution = new Resolution(
            tenant.Id,
            tenant.Slug,
            principalId,
            string.IsNullOrWhiteSpace(principalName) ? principalId : principalName,
            roles.ToHashSet(StringComparer.Ordinal),
            correlationId);
    }

    private sealed record Resolution(
        Guid TenantId, string TenantSlug, string PrincipalId, string PrincipalName, IReadOnlySet<string> Roles, string CorrelationId)
        : ITenantContext
    {
        public bool IsResolved => true;

        public string KnowledgeNamespace => $"tenant-{TenantSlug}";

        // A request principal is a staff user or a claimant, never the adjudication service.
        public bool IsSystem => false;
    }
}
