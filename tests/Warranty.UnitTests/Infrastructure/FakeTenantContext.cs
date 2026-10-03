using Warranty.Application.Abstractions;

namespace Warranty.UnitTests.Infrastructure;

/// <summary>An Aurora tenant context, or an unresolved one when <paramref name="tenantId"/> is null.</summary>
internal sealed class FakeTenantContext(Guid? tenantId) : ITenantContext
{
    public bool IsResolved => tenantId.HasValue;

    public Guid TenantId => tenantId ?? throw new InvalidOperationException("No tenant.");

    public string TenantSlug => "aurora";

    public string KnowledgeNamespace => "tenant-aurora";

    public string PrincipalId => "test";

    public string PrincipalName => "test";

    public IReadOnlySet<string> Roles { get; } = new HashSet<string>();

    public string CorrelationId => "test";

    public bool IsSystem => false;
}
