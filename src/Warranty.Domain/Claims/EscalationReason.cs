using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace Warranty.Domain.Claims;

/// <summary>Why a claim was routed to human review (FR-028, FR-031, research R24/R25).</summary>
public enum EscalationReason
{
    [JsonStringEnumMemberName("VALUE_ABOVE_LIMIT")]
    ValueAboveLimit,

    [JsonStringEnumMemberName("CONFIDENCE_BELOW_MIN")]
    ConfidenceBelowMin,

    [JsonStringEnumMemberName("ALWAYS_REVIEW_CATEGORY")]
    AlwaysReviewCategory,

    [JsonStringEnumMemberName("RISK_MEDIUM")]
    RiskMedium,

    [JsonStringEnumMemberName("RISK_HIGH")]
    RiskHigh,

    [JsonStringEnumMemberName("EVIDENCE_CONFLICT")]
    EvidenceConflict,

    [JsonStringEnumMemberName("AI_DETERMINISTIC_DISAGREEMENT")]
    AiDeterministicDisagreement,

    [JsonStringEnumMemberName("INVALID_RECOMMENDATION")]
    InvalidRecommendation,

    [JsonStringEnumMemberName("AI_UNAVAILABLE")]
    AiUnavailable,

    [JsonStringEnumMemberName("AI_RECOMMENDS_REVIEW")]
    AiRecommendsReview,

    [JsonStringEnumMemberName("NO_APPLICABLE_POLICY")]
    NoApplicablePolicy,

    [JsonStringEnumMemberName("AMBIGUOUS_POLICY")]
    AmbiguousPolicy,

    [JsonStringEnumMemberName("PRODUCT_NOT_IN_CATALOG")]
    ProductNotInCatalog,

    [JsonStringEnumMemberName("RETURNED_AFTER_REVIEWER_REQUEST")]
    ReturnedAfterReviewerRequest,

    [JsonStringEnumMemberName("INFO_INCOMPLETE_AFTER_2_REQUESTS")]
    InfoIncompleteAfterTwoRequests,

    [JsonStringEnumMemberName("UNSAFE_CLAIMANT_TEXT")]
    UnsafeClaimantText,
}

public static class EscalationReasonExtensions
{
    /// <summary>Label shown to claims agents in place of any risk-related reason (FR-005).</summary>
    public const string AgentSafeLabel = "Additional checks required";

    private static readonly FrozenDictionary<EscalationReason, string> Labels = new Dictionary<EscalationReason, string>
    {
        [EscalationReason.ValueAboveLimit] = "claim value above auto-approval limit",
        [EscalationReason.ConfidenceBelowMin] = "confidence below the tenant's minimum",
        [EscalationReason.AlwaysReviewCategory] = "product category always requires a human decision",
        [EscalationReason.RiskMedium] = "medium risk",
        [EscalationReason.RiskHigh] = "high risk",
        [EscalationReason.EvidenceConflict] = "conflicting evidence",
        [EscalationReason.AiDeterministicDisagreement] = "AI recommendation conflicts with an independent check",
        [EscalationReason.InvalidRecommendation] = "AI recommendation invalid",
        [EscalationReason.AiUnavailable] = "AI analysis could not be completed",
        [EscalationReason.AiRecommendsReview] = "AI recommends human review",
        [EscalationReason.NoApplicablePolicy] = "no applicable policy found",
        [EscalationReason.AmbiguousPolicy] = "policy applicability is ambiguous",
        [EscalationReason.ProductNotInCatalog] = "product or serial not in the tenant's catalog",
        [EscalationReason.ReturnedAfterReviewerRequest] = "returned after reviewer information request",
        [EscalationReason.InfoIncompleteAfterTwoRequests] = "information still incomplete after 2 requests",
        [EscalationReason.UnsafeClaimantText] = "claimant explanation needs a reviewer",
    }.ToFrozenDictionary();

    /// <summary>Readable label shown to reviewers and auditors.</summary>
    public static string Label(this EscalationReason reason)
        => Labels.TryGetValue(reason, out var label)
            ? label
            : throw new ArgumentOutOfRangeException(nameof(reason), reason, null);

    /// <summary>Reasons that would hint at risk or fraud indicators and are hidden from claims agents (FR-005).</summary>
    public static bool IsRiskRelated(this EscalationReason reason)
        => reason is EscalationReason.RiskMedium
            or EscalationReason.RiskHigh
            or EscalationReason.EvidenceConflict
            or EscalationReason.AiDeterministicDisagreement
            or EscalationReason.UnsafeClaimantText;
}
