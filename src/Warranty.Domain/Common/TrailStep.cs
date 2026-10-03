namespace Warranty.Domain.Common;

/// <summary>Step recorded by one append-only decision trail entry (FR-038).</summary>
public enum TrailStep
{
    ClaimSubmitted,
    TenantResolved,
    EvidenceStored,
    IntakeValidated,
    ClaimExtracted,
    CustomerVerified,
    ProductIdentified,
    PolicyRetrieved,
    EvidenceAnalyzed,
    CoverageAssessed,
    RiskEvaluated,
    AiRecommended,
    GuardrailsEvaluated,
    AutoApproved,
    AutoRejected,
    InformationRequested,
    EscalatedToReview,
    ReviewerDecided,
    SupplementReceived,
    ActionExecuted,
    AiStepFailed,
    Correction,
}
