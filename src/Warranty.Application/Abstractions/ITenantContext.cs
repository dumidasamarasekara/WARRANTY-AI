namespace Warranty.Application.Abstractions;

/// <summary>
/// The tenant and principal of the current request or background job, established from trusted
/// context only — the staff token, the claimant channel host, or the job record — never from request
/// bodies or model output (research R9, constitution V). Data access, retrieval, tools and traces all
/// read the tenant from here; no port takes a tenant ID where this context applies.
/// </summary>
public interface ITenantContext
{
    /// <summary>True once a tenant has been resolved for the current scope.</summary>
    bool IsResolved { get; }

    /// <summary>The current tenant; throws when no tenant has been resolved.</summary>
    Guid TenantId { get; }

    string TenantSlug { get; }

    /// <summary>The tenant's knowledge namespace, <c>tenant-{slug}</c>.</summary>
    string KnowledgeNamespace { get; }

    /// <summary>Staff <c>sub</c>, <c>claimant:{claimId}</c>, or <c>adjudication-service</c>.</summary>
    string PrincipalId { get; }

    string PrincipalName { get; }

    IReadOnlySet<string> Roles { get; }

    string CorrelationId { get; }

    /// <summary>True for the background adjudication service principal.</summary>
    bool IsSystem { get; }
}

/// <summary>Well-known principal values.</summary>
public static class Principals
{
    /// <summary>System principal used by the claim job worker (tasks.md fixed identifiers).</summary>
    public const string AdjudicationService = "adjudication-service";

    public const string ClaimsAgentRole = "claims-agent";

    public const string ClaimsReviewerRole = "claims-reviewer";

    public const string AuditorRole = "auditor";
}
