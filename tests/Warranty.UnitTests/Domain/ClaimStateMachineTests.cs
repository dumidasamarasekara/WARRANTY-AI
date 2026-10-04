using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.UnitTests.Domain;

/// <summary>The claim state machine and its loop limits (data-model.md "Claim state machine", research R24).</summary>
public sealed class ClaimStateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 9, 41, 0, TimeSpan.Zero);
    private static readonly RequestedItem[] Items = [RequestedItem.Create("LEGIBLE_INVOICE", "The invoice photo is unreadable.")];

    /// <summary>Every state-changing method of <see cref="Claim"/>, as one caller (the ActionExecutor or the worker) would use it.</summary>
    public enum ClaimAction
    {
        StartEvaluation,
        SystemRequestInformation,
        ReviewerRequestInformation,
        EscalateToReview,
        SystemApprove,
        SystemReject,
        ReviewerApprove,
        ReviewerReject,
        AddSupplement,
    }

    /// <summary>The allowed edges of the state machine; every other (status, action) pair is refused.</summary>
    private static readonly Dictionary<ClaimStatus, (ClaimAction Action, ClaimStatus To)[]> AllowedTransitions = new()
    {
        [ClaimStatus.Submitted] = [(ClaimAction.StartEvaluation, ClaimStatus.UnderEvaluation)],
        [ClaimStatus.UnderEvaluation] =
        [
            (ClaimAction.StartEvaluation, ClaimStatus.UnderEvaluation), // a retried or resumed job
            (ClaimAction.SystemRequestInformation, ClaimStatus.PendingInformation),
            (ClaimAction.EscalateToReview, ClaimStatus.UnderReview),
            (ClaimAction.SystemApprove, ClaimStatus.Approved),
            (ClaimAction.SystemReject, ClaimStatus.Rejected),
        ],
        [ClaimStatus.UnderReview] =
        [
            (ClaimAction.ReviewerRequestInformation, ClaimStatus.PendingInformation),
            (ClaimAction.ReviewerApprove, ClaimStatus.Approved),
            (ClaimAction.ReviewerReject, ClaimStatus.Rejected),
        ],
        [ClaimStatus.PendingInformation] = [(ClaimAction.AddSupplement, ClaimStatus.UnderEvaluation)],
        [ClaimStatus.Approved] = [],
        [ClaimStatus.Rejected] = [],
    };

    public static TheoryData<ClaimStatus, ClaimAction, ClaimStatus> AllowedEdges
    {
        get
        {
            var data = new TheoryData<ClaimStatus, ClaimAction, ClaimStatus>();
            foreach (var (from, edges) in AllowedTransitions)
            {
                foreach (var (action, to) in edges)
                {
                    data.Add(from, action, to);
                }
            }

            return data;
        }
    }

    public static TheoryData<ClaimStatus, ClaimAction> RefusedEdges
    {
        get
        {
            var data = new TheoryData<ClaimStatus, ClaimAction>();
            foreach (var from in Enum.GetValues<ClaimStatus>())
            {
                foreach (var action in Enum.GetValues<ClaimAction>().Where(a => AllowedTransitions[from].All(edge => edge.Action != a)))
                {
                    data.Add(from, action);
                }
            }

            return data;
        }
    }

    [Fact]
    public void A_submitted_claim_starts_in_round_one_with_no_requests_and_no_outcome()
    {
        var claim = NewClaim();

        claim.Status.ShouldBe(ClaimStatus.Submitted);
        claim.CurrentRound.ShouldBe(1);
        claim.AutoInfoRequestCount.ShouldBe(0);
        claim.ReviewerInfoRequested.ShouldBeFalse();
        claim.RequestedItems.ShouldBeEmpty();
        claim.FinalOutcome.ShouldBeNull();
        claim.FinalDecidedBy.ShouldBeNull();
        claim.FinalizedAt.ShouldBeNull();
    }

    [Fact]
    public void The_transition_table_covers_every_status()
        => AllowedTransitions.Keys.ShouldBe(Enum.GetValues<ClaimStatus>(), ignoreOrder: true);

    [Theory]
    [MemberData(nameof(AllowedEdges))]
    public void Allowed_transitions_move_the_claim(ClaimStatus from, ClaimAction action, ClaimStatus to)
    {
        var claim = ClaimIn(from);

        Apply(claim, action);

        claim.Status.ShouldBe(to);
        claim.UpdatedAt.ShouldBe(Later);
    }

    [Theory]
    [MemberData(nameof(RefusedEdges))]
    public void Every_other_transition_is_refused_and_changes_nothing(ClaimStatus from, ClaimAction action)
    {
        var claim = ClaimIn(from);
        var round = claim.CurrentRound;
        var count = claim.AutoInfoRequestCount;
        var reviewerFlag = claim.ReviewerInfoRequested;
        var items = claim.RequestedItems;

        Should.Throw<InvalidClaimTransitionException>(() => Apply(claim, action));

        claim.Status.ShouldBe(from);
        claim.CurrentRound.ShouldBe(round);
        claim.AutoInfoRequestCount.ShouldBe(count);
        claim.ReviewerInfoRequested.ShouldBe(reviewerFlag);
        claim.RequestedItems.ShouldBe(items);
    }

    [Theory]
    [InlineData(ClaimStatus.Approved)]
    [InlineData(ClaimStatus.Rejected)]
    public void Terminal_claims_refuse_supplements(ClaimStatus terminal)
    {
        var claim = ClaimIn(terminal);
        terminal.IsFinal().ShouldBeTrue();

        var refused = Should.Throw<InvalidClaimTransitionException>(() => claim.AddSupplement(Later));

        refused.From.ShouldBe(terminal);
        claim.CurrentRound.ShouldBe(1);
    }

    [Fact]
    public void A_system_information_request_counts_toward_the_automatic_limit()
    {
        var claim = ClaimIn(ClaimStatus.UnderEvaluation);

        claim.RequestInformation(Items, DecidedBy.System, Later);

        claim.Status.ShouldBe(ClaimStatus.PendingInformation);
        claim.AutoInfoRequestCount.ShouldBe(1);
        claim.ReviewerInfoRequested.ShouldBeFalse();
        claim.RequestedItems.ShouldBe(Items);
    }

    [Fact]
    public void A_third_automatic_information_request_is_refused()
    {
        var claim = ClaimIn(ClaimStatus.UnderEvaluation);
        for (var request = 1; request <= Claim.MaxAutomaticInformationRequests; request++)
        {
            claim.RequestInformation(Items, DecidedBy.System, Later);
            claim.AutoInfoRequestCount.ShouldBe(request);
            claim.AddSupplement(Later);
        }

        claim.CurrentRound.ShouldBe(3);
        claim.Status.ShouldBe(ClaimStatus.UnderEvaluation);

        Should.Throw<InvalidClaimTransitionException>(() => claim.RequestInformation(Items, DecidedBy.System, Later));

        claim.AutoInfoRequestCount.ShouldBe(2);
        claim.Status.ShouldBe(ClaimStatus.UnderEvaluation);
        claim.RequestedItems.ShouldBeEmpty();

        // The run then ends with a reviewer instead (reason INFO_INCOMPLETE_AFTER_2_REQUESTS).
        claim.EscalateToReview(Later);
        claim.Status.ShouldBe(ClaimStatus.UnderReview);
    }

    [Fact]
    public void A_reviewer_information_request_sets_the_reviewer_flag_without_touching_the_count()
    {
        var claim = ClaimIn(ClaimStatus.UnderReview);

        claim.RequestInformation(Items, DecidedBy.Reviewer, Later);

        claim.Status.ShouldBe(ClaimStatus.PendingInformation);
        claim.ReviewerInfoRequested.ShouldBeTrue();
        claim.AutoInfoRequestCount.ShouldBe(0);
        claim.RequestedItems.ShouldBe(Items);
    }

    [Fact]
    public void The_reviewer_flag_stays_set_after_the_supplement_and_later_rounds()
    {
        var claim = ClaimIn(ClaimStatus.UnderReview);
        claim.RequestInformation(Items, DecidedBy.Reviewer, Later);

        claim.AddSupplement(Later);

        claim.Status.ShouldBe(ClaimStatus.UnderEvaluation);
        claim.CurrentRound.ShouldBe(2);
        claim.RequestedItems.ShouldBeEmpty();
        claim.ReviewerInfoRequested.ShouldBeTrue();

        // Returned to review (RETURNED_AFTER_REVIEWER_REQUEST) and finalized: the flag is never cleared.
        claim.EscalateToReview(Later);
        claim.FinalizeApproved("Your tablet is covered for manufacturing defects.", DecidedBy.Reviewer, Later);
        claim.ReviewerInfoRequested.ShouldBeTrue();
    }

    [Fact]
    public void A_supplement_starts_a_new_round_and_clears_the_requested_items()
    {
        var claim = ClaimIn(ClaimStatus.PendingInformation);
        claim.RequestedItems.ShouldNotBeEmpty();

        claim.AddSupplement(Later);

        claim.CurrentRound.ShouldBe(2);
        claim.RequestedItems.ShouldBeEmpty();
        claim.Status.ShouldBe(ClaimStatus.UnderEvaluation);
    }

    [Theory]
    [InlineData(FinalOutcome.Approved, DecidedBy.System)]
    [InlineData(FinalOutcome.Rejected, DecidedBy.System)]
    [InlineData(FinalOutcome.Approved, DecidedBy.Reviewer)]
    [InlineData(FinalOutcome.Rejected, DecidedBy.Reviewer)]
    public void Finalizing_records_the_outcome_the_decider_the_explanation_and_the_time(FinalOutcome outcome, DecidedBy decidedBy)
    {
        var claim = ClaimIn(decidedBy == DecidedBy.System ? ClaimStatus.UnderEvaluation : ClaimStatus.UnderReview);

        Finalize(claim, outcome, decidedBy, "  Your tablet is covered for manufacturing defects for 12 months.  ");

        claim.Status.ShouldBe(outcome == FinalOutcome.Approved ? ClaimStatus.Approved : ClaimStatus.Rejected);
        claim.Status.IsFinal().ShouldBeTrue();
        claim.FinalOutcome.ShouldBe(outcome);
        claim.FinalDecidedBy.ShouldBe(decidedBy);
        claim.FinalExplanation.ShouldBe("Your tablet is covered for manufacturing defects for 12 months.");
        claim.FinalizedAt.ShouldBe(Later);
        claim.RequestedItems.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(FinalOutcome.Approved, DecidedBy.System, "")]
    [InlineData(FinalOutcome.Rejected, DecidedBy.System, "   ")]
    [InlineData(FinalOutcome.Approved, DecidedBy.Reviewer, " ")]
    [InlineData(FinalOutcome.Rejected, DecidedBy.Reviewer, "")]
    public void Finalizing_without_an_explanation_is_refused(FinalOutcome outcome, DecidedBy decidedBy, string explanation)
    {
        var from = decidedBy == DecidedBy.System ? ClaimStatus.UnderEvaluation : ClaimStatus.UnderReview;
        var claim = ClaimIn(from);

        Should.Throw<ArgumentException>(() => Finalize(claim, outcome, decidedBy, explanation));

        claim.Status.ShouldBe(from);
        claim.FinalOutcome.ShouldBeNull();
        claim.FinalizedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(DecidedBy.System)]
    [InlineData(DecidedBy.Reviewer)]
    public void An_information_request_needs_at_least_one_item(DecidedBy requestedBy)
    {
        var from = requestedBy == DecidedBy.System ? ClaimStatus.UnderEvaluation : ClaimStatus.UnderReview;
        var claim = ClaimIn(from);

        Should.Throw<ArgumentException>(() => claim.RequestInformation([], requestedBy, Later));

        claim.Status.ShouldBe(from);
        claim.AutoInfoRequestCount.ShouldBe(0);
        claim.ReviewerInfoRequested.ShouldBeFalse();
    }

    private static DateTimeOffset Later => T0.AddHours(1);

    private static Claim NewClaim() => Claim.Submit(
        Guid.CreateVersion7(),
        Guid.Parse("11111111-1111-7111-8111-111111111111"),
        ClaimReference.Generate(),
        ClaimChannel.ClaimantPortal,
        Claim.ClaimantSubmitter,
        Guid.CreateVersion7(),
        "claimant@example.test",
        null,
        "AUR-TAB-10",
        Guid.CreateVersion7(),
        "AT10-000123",
        new DateOnly(2026, 3, 14),
        "Aurora Store",
        450.00m,
        Region.NA,
        "The screen stays black after the latest charge and the device does not start.",
        T0);

    /// <summary>A claim brought to <paramref name="status"/> through allowed transitions only.</summary>
    private static Claim ClaimIn(ClaimStatus status)
    {
        var claim = NewClaim();
        switch (status)
        {
            case ClaimStatus.Submitted:
                break;
            case ClaimStatus.UnderEvaluation:
                claim.StartEvaluation(T0);
                break;
            case ClaimStatus.PendingInformation:
                claim.StartEvaluation(T0);
                claim.RequestInformation(Items, DecidedBy.System, T0);
                break;
            case ClaimStatus.UnderReview:
                claim.StartEvaluation(T0);
                claim.EscalateToReview(T0);
                break;
            case ClaimStatus.Approved:
                claim.StartEvaluation(T0);
                claim.FinalizeApproved("Covered for manufacturing defects.", DecidedBy.System, T0);
                break;
            case ClaimStatus.Rejected:
                claim.StartEvaluation(T0);
                claim.EscalateToReview(T0);
                claim.FinalizeRejected("Accidental damage is not covered.", DecidedBy.Reviewer, T0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }

        claim.Status.ShouldBe(status);
        return claim;
    }

    private static void Apply(Claim claim, ClaimAction action)
    {
        switch (action)
        {
            case ClaimAction.StartEvaluation:
                claim.StartEvaluation(Later);
                break;
            case ClaimAction.SystemRequestInformation:
                claim.RequestInformation(Items, DecidedBy.System, Later);
                break;
            case ClaimAction.ReviewerRequestInformation:
                claim.RequestInformation(Items, DecidedBy.Reviewer, Later);
                break;
            case ClaimAction.EscalateToReview:
                claim.EscalateToReview(Later);
                break;
            case ClaimAction.SystemApprove:
                claim.FinalizeApproved("Covered.", DecidedBy.System, Later);
                break;
            case ClaimAction.SystemReject:
                claim.FinalizeRejected("Not covered.", DecidedBy.System, Later);
                break;
            case ClaimAction.ReviewerApprove:
                claim.FinalizeApproved("Covered.", DecidedBy.Reviewer, Later);
                break;
            case ClaimAction.ReviewerReject:
                claim.FinalizeRejected("Not covered.", DecidedBy.Reviewer, Later);
                break;
            case ClaimAction.AddSupplement:
                claim.AddSupplement(Later);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    private static void Finalize(Claim claim, FinalOutcome outcome, DecidedBy decidedBy, string explanation)
    {
        if (outcome == FinalOutcome.Approved)
        {
            claim.FinalizeApproved(explanation, decidedBy, Later);
        }
        else
        {
            claim.FinalizeRejected(explanation, decidedBy, Later);
        }
    }
}
