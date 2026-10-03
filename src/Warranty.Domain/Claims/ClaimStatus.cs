namespace Warranty.Domain.Claims;

/// <summary>Claim lifecycle state (data-model.md "Claim state machine"); claimant-visible (FR-037).</summary>
public enum ClaimStatus
{
    Submitted,
    UnderEvaluation,
    PendingInformation,
    UnderReview,
    Approved,
    Rejected,
}

public static class ClaimStatusExtensions
{
    public static bool IsFinal(this ClaimStatus status)
        => status is ClaimStatus.Approved or ClaimStatus.Rejected;
}
