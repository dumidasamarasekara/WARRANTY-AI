using Warranty.AI.Harness.Agents.Risk;
using Warranty.Domain.Claims;

namespace Warranty.UnitTests.Harness;

/// <summary>The <c>DUPLICATE_SERIAL_CLAIM</c> window (research R25).</summary>
public sealed class DuplicateClaimWindowTests
{
    private const string Pending = "Pending T060";
    private static readonly DateOnly ClaimDate = new(2026, 9, 30);
    private static readonly Guid ThisClaim = Guid.Parse("0199a000-0000-7000-8000-0000000000c1");
    private static readonly Guid OtherClaim = Guid.Parse("0199a000-0000-7000-8000-0000000000c2");

    [Fact]
    public void The_window_is_90_days()
        => DuplicateClaimWindow.DuplicateClaimWindowDays.ShouldBe(90);

    [Theory(Skip = Pending)]
    [InlineData(ClaimStatus.Submitted)]
    [InlineData(ClaimStatus.UnderEvaluation)]
    [InlineData(ClaimStatus.PendingInformation)]
    [InlineData(ClaimStatus.UnderReview)]
    public void Another_claim_that_is_not_final_is_a_duplicate(ClaimStatus status)
        => DuplicateClaimWindow.IsDuplicate(status, otherFinalizedAt: null, ClaimDate).ShouldBeTrue();

    [Theory(Skip = Pending)]
    [InlineData(ClaimStatus.Approved, 89, true)]
    [InlineData(ClaimStatus.Approved, 90, true)]
    [InlineData(ClaimStatus.Approved, 91, false)]
    [InlineData(ClaimStatus.Rejected, 89, true)]
    [InlineData(ClaimStatus.Rejected, 90, true)]
    [InlineData(ClaimStatus.Rejected, 91, false)]
    [InlineData(ClaimStatus.Approved, 365, false)]
    public void A_final_claim_is_a_duplicate_only_within_90_days_before_the_claim_date(ClaimStatus status, int daysBefore, bool duplicate)
        => DuplicateClaimWindow.IsDuplicate(status, FinalizedDaysBefore(daysBefore), ClaimDate).ShouldBe(duplicate);

    [Fact(Skip = Pending)]
    public void A_claim_finalized_after_the_claim_date_was_open_when_the_claim_was_made()
        => DuplicateClaimWindow.IsDuplicate(ClaimStatus.Approved, FinalizedDaysBefore(-5), ClaimDate).ShouldBeTrue();

    [Fact(Skip = Pending)]
    public void Earlier_rounds_of_the_same_claim_never_count()
    {
        SerialClaim[] claimsForSerial =
        [
            new(ThisClaim, ClaimStatus.PendingInformation, null),
            new(ThisClaim, ClaimStatus.UnderEvaluation, null),
        ];

        DuplicateClaimWindow.CountDuplicates(ThisClaim, ClaimDate, claimsForSerial).ShouldBe(0);
    }

    [Fact(Skip = Pending)]
    public void Duplicates_are_counted_among_other_claims_only()
    {
        SerialClaim[] claimsForSerial =
        [
            new(ThisClaim, ClaimStatus.UnderEvaluation, null),
            new(OtherClaim, ClaimStatus.UnderReview, null),
            new(Guid.NewGuid(), ClaimStatus.Rejected, FinalizedDaysBefore(90)),
            new(Guid.NewGuid(), ClaimStatus.Approved, FinalizedDaysBefore(91)),
        ];

        DuplicateClaimWindow.CountDuplicates(ThisClaim, ClaimDate, claimsForSerial).ShouldBe(2);
    }

    // Late in the day, so the boundary is the calendar day, not the time of day.
    private static DateTimeOffset FinalizedDaysBefore(int days)
        => new(ClaimDate.AddDays(-days).ToDateTime(new TimeOnly(23, 30)), TimeSpan.Zero);
}
