using Warranty.Domain.Claims;

namespace Warranty.AI.Harness.Agents.Risk;

/// <summary>Another claim of the same tenant and serial, as seen by the duplicate rule.</summary>
/// <param name="ClaimId">The other claim; rounds of one claim share its ID.</param>
/// <param name="Status">Its current status.</param>
/// <param name="FinalizedAt">When it reached <c>Approved</c>/<c>Rejected</c>; null while not final.</param>
public sealed record SerialClaim(Guid ClaimId, ClaimStatus Status, DateTimeOffset? FinalizedAt);

/// <summary>
/// The <c>DUPLICATE_SERIAL_CLAIM</c> rule (research R25): another claim with the same serial is a
/// duplicate when it is not final or was finalized within <see cref="DuplicateClaimWindowDays"/> days
/// before this claim's claim date. Earlier rounds of the same claim are the same claim and never count.
/// Pure; used by <c>claim_history_lookup</c> (<c>duplicateClaimsForSerial</c>).
/// </summary>
public static class DuplicateClaimWindow
{
    /// <summary>A claim finalized this many days before the claim date is still a duplicate.</summary>
    public const int DuplicateClaimWindowDays = 90;

    /// <summary>
    /// Whether another claim of the same serial counts as a duplicate of a claim made on <paramref name="claimDate"/>.
    /// The window is in calendar days (UTC): a claim finalized on any time of the day
    /// <c>claimDate − 90</c> or later is a duplicate, including one finalized after the claim date (it
    /// was still open when this claim was made). A final claim without a finalization time is not.
    /// </summary>
    public static bool IsDuplicate(ClaimStatus otherClaimStatus, DateTimeOffset? otherFinalizedAt, DateOnly claimDate)
    {
        if (!otherClaimStatus.IsFinal())
        {
            return true;
        }

        return otherFinalizedAt is { } finalizedAt
            && DateOnly.FromDateTime(finalizedAt.UtcDateTime) >= claimDate.AddDays(-DuplicateClaimWindowDays);
    }

    /// <summary>Number of duplicates among the claims of the serial, excluding the claim <paramref name="claimId"/> itself.</summary>
    public static int CountDuplicates(Guid claimId, DateOnly claimDate, IEnumerable<SerialClaim> claimsForSerial)
    {
        ArgumentNullException.ThrowIfNull(claimsForSerial);
        return claimsForSerial
            .Where(c => c.ClaimId != claimId)
            .DistinctBy(c => c.ClaimId)
            .Count(c => IsDuplicate(c.Status, c.FinalizedAt, claimDate));
    }
}
