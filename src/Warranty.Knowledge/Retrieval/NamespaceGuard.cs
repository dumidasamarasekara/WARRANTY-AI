using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Knowledge;

namespace Warranty.Knowledge.Retrieval;

/// <summary>
/// Namespace and principal rules of tenant-aware retrieval (contracts/rag.md, research R7). Everything
/// is derived from <see cref="ITenantContext"/>: the tenant namespace, the roles and the readable
/// classifications. The post-retrieval assertion re-checks every returned chunk against the same
/// context, independently of the store's own filters and row-level security.
/// </summary>
public static class NamespaceGuard
{
    public const string GlobalNamespace = "global";

    private const string ClaimantPrincipalPrefix = "claimant:";

    /// <summary>Retrieval never runs without an established tenant context.</summary>
    public static void RequireTenant(ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (!tenant.IsResolved)
        {
            throw new InvalidOperationException("Knowledge retrieval requires a resolved tenant context.");
        }
    }

    /// <summary>The namespaces a scope may read: the context tenant's, plus <c>global</c> when the scope includes it.</summary>
    public static IReadOnlyList<string> NamespacesFor(ITenantContext tenant, KnowledgeScope scope)
    {
        RequireTenant(tenant);
        return scope switch
        {
            KnowledgeScope.Global => [GlobalNamespace],
            KnowledgeScope.Tenant => [tenant.KnowledgeNamespace],
            KnowledgeScope.GlobalAndTenant => [tenant.KnowledgeNamespace, GlobalNamespace],
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown knowledge scope."),
        };
    }

    /// <summary>
    /// Classifications the principal may read: claimants only <c>Public</c>; the adjudication service,
    /// reviewers and auditors everything; other staff <c>Public</c> and <c>Internal</c>.
    /// </summary>
    public static IReadOnlyList<DocumentClassification> ClassificationsFor(ITenantContext tenant)
    {
        RequireTenant(tenant);
        if (tenant.PrincipalId.StartsWith(ClaimantPrincipalPrefix, StringComparison.Ordinal))
        {
            return [DocumentClassification.Public];
        }

        return tenant.IsSystem
               || tenant.Roles.Contains(Principals.ClaimsReviewerRole)
               || tenant.Roles.Contains(Principals.AuditorRole)
            ? [DocumentClassification.Public, DocumentClassification.Internal, DocumentClassification.Confidential]
            : [DocumentClassification.Public, DocumentClassification.Internal];
    }

    /// <summary>
    /// The chunks that break the post-retrieval assertion: a namespace outside <paramref name="allowedNamespaces"/>,
    /// a tenant chunk of another tenant, or a <c>global</c> chunk that belongs to a tenant.
    /// </summary>
    public static IReadOnlyList<RetrievedChunk> Violations(
        ITenantContext tenant, IReadOnlyCollection<string> allowedNamespaces, IEnumerable<RetrievedChunk> chunks)
    {
        RequireTenant(tenant);
        ArgumentNullException.ThrowIfNull(allowedNamespaces);
        ArgumentNullException.ThrowIfNull(chunks);
        return chunks.Where(chunk => !IsAllowed(tenant, allowedNamespaces, chunk)).ToList();
    }

    /// <summary>True when a namespace returned by the store is one the query was allowed to read.</summary>
    public static bool IsAllowedNamespace(IReadOnlyCollection<string> allowedNamespaces, string knowledgeNamespace)
        => allowedNamespaces.Contains(knowledgeNamespace, StringComparer.Ordinal);

    private static bool IsAllowed(ITenantContext tenant, IReadOnlyCollection<string> allowedNamespaces, RetrievedChunk chunk)
    {
        if (!IsAllowedNamespace(allowedNamespaces, chunk.Namespace))
        {
            return false;
        }

        return chunk.Namespace == GlobalNamespace
            ? chunk.TenantId is null
            : chunk.Namespace == tenant.KnowledgeNamespace && chunk.TenantId == tenant.TenantId;
    }
}
