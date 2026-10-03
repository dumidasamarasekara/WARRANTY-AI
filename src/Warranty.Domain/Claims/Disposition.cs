namespace Warranty.Domain.Claims;

/// <summary>Outcome of the deterministic guardrail evaluation of one adjudication run (FR-024 – FR-029).</summary>
public enum Disposition
{
    AutoApprove,
    AutoReject,
    RequestInformation,
    HumanReview,
}
