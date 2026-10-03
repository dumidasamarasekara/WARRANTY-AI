using System.Text.Json.Serialization;

namespace Warranty.Domain.Claims;

/// <summary>Deterministic guardrail checks, recorded in order on every evaluation (FR-025, FR-030).</summary>
public enum GuardrailCheckCode
{
    [JsonStringEnumMemberName("SCHEMA_VALID")]
    SchemaValid,

    [JsonStringEnumMemberName("REFERENCES_VALID")]
    ReferencesValid,

    [JsonStringEnumMemberName("REQUIRED_INFO_COMPLETE")]
    RequiredInfoComplete,

    [JsonStringEnumMemberName("PRODUCT_IN_CATALOG")]
    ProductInCatalog,

    [JsonStringEnumMemberName("POLICY_APPLICABLE")]
    PolicyApplicable,

    [JsonStringEnumMemberName("COVERAGE_WINDOW_AGREES")]
    CoverageWindowAgrees,

    [JsonStringEnumMemberName("CLAIM_VALUE_WITHIN_LIMIT")]
    ClaimValueWithinLimit,

    [JsonStringEnumMemberName("CONFIDENCE_AT_OR_ABOVE_MIN")]
    ConfidenceAtOrAboveMin,

    /// <summary>Passes only when no risk signal of any kind is present (research R23).</summary>
    [JsonStringEnumMemberName("RISK_LOW")]
    RiskLow,

    [JsonStringEnumMemberName("NO_CONFLICTS")]
    NoConflicts,

    [JsonStringEnumMemberName("NO_MANIPULATION")]
    NoManipulation,

    [JsonStringEnumMemberName("CATEGORY_NOT_ALWAYS_REVIEW")]
    CategoryNotAlwaysReview,

    /// <summary>A REJECT is grounded in a confirmed period clause or an evidenced exclusion (research R26).</summary>
    [JsonStringEnumMemberName("GROUNDED_IN_CLAUSE")]
    GroundedInClause,

    [JsonStringEnumMemberName("AUTO_DECISION_ENABLED")]
    AutoDecisionEnabled,

    [JsonStringEnumMemberName("ACTOR_AUTHORIZED")]
    ActorAuthorized,

    /// <summary>Fails once a reviewer has requested more information on the claim (research R24).</summary>
    [JsonStringEnumMemberName("NOT_RETURNED_FROM_REVIEW")]
    NotReturnedFromReview,

    /// <summary>Fails when a third automatic information request would be needed (research R24).</summary>
    [JsonStringEnumMemberName("AUTO_INFO_REQUESTS_WITHIN_LIMIT")]
    AutoInfoRequestsWithinLimit,

    /// <summary>The AI's claimant explanation contains no risk/fraud terms or reference IDs (research R25).</summary>
    [JsonStringEnumMemberName("CLAIMANT_TEXT_SAFE")]
    ClaimantTextSafe,
}
