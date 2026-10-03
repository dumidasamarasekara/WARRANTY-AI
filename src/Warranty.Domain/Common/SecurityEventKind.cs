using System.Text.Json.Serialization;

namespace Warranty.Domain.Common;

/// <summary>
/// Kind of an append-only security event (FR-005, FR-037a, FR-041a). Operator-only kinds are
/// stored with no tenant and are never returned to tenant auditors (research R30).
/// </summary>
public enum SecurityEventKind
{
    /// <summary>Staff lookup of an ID not visible in the actor's tenant; identical for unknown and cross-tenant IDs.</summary>
    [JsonStringEnumMemberName("ACCESS_DENIED")]
    AccessDenied,

    /// <summary>Operator-only companion of <see cref="AccessDenied"/> when the ID belongs to another tenant.</summary>
    [JsonStringEnumMemberName("CROSS_TENANT_ACCESS_DENIED")]
    CrossTenantAccessDenied,

    [JsonStringEnumMemberName("CLAIMANT_ACCESS_FAILED")]
    ClaimantAccessFailed,

    [JsonStringEnumMemberName("RETRIEVAL_SCOPE_VIOLATION")]
    RetrievalScopeViolation,

    [JsonStringEnumMemberName("TOOL_SCOPE_VIOLATION")]
    ToolScopeViolation,

    [JsonStringEnumMemberName("SELF_REVIEW_REFUSED")]
    SelfReviewRefused,

    /// <summary>Operator-only: a claimant request arrived on a host that maps to no tenant.</summary>
    [JsonStringEnumMemberName("UNKNOWN_CHANNEL")]
    UnknownChannel,
}

public static class SecurityEventKindExtensions
{
    /// <summary>Operator-only kinds are written with <c>tenant_id = NULL</c> and hidden from tenants.</summary>
    public static bool IsOperatorOnly(this SecurityEventKind kind)
        => kind is SecurityEventKind.CrossTenantAccessDenied or SecurityEventKind.UnknownChannel;
}
