using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;
using Warranty.Application;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Actions;
using Warranty.Application.Claims;
using Warranty.Application.Review;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Review;

namespace Warranty.UnitTests.Application;

/// <summary>
/// Reviewer decisions (T087): <see cref="RecordReviewDecision"/> with the real <see cref="ActionExecutor"/>
/// behind it — separation of duties, the If-Match row-version check, the UnderReview precondition,
/// <c>overridesAi</c> and the justification / claimant explanation / requested items rules (including
/// the claimant text screen), the claim transitions and the <c>ReviewerDecided</c> trail entry.
/// </summary>
public sealed class RecordReviewDecisionTests
{
    private const string ReviewerSub = "reviewer-sub-1";
    private const string ReviewerName = "Aurora Reviewer";
    private const string SubmittingAgentSub = "agent-sub-1";
    private const uint RowVersion = 4711;
    private const string ValidIfMatch = "\"4711\"";
    private const string Justification = "Invoice and photos confirm a manufacturing defect.";
    private const string ApproveText = "Your tablet is covered for manufacturing defects and will be repaired free of charge.";
    private const string RejectText = "Your tablet's warranty period ended before the claim date, so this repair is not covered.";

    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OtherTenant = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid CustomerId = Guid.Parse("0199b000-0000-7000-8000-0000000000d7");
    private static readonly Guid ProductId = Guid.Parse("0199b000-0000-7000-8000-0000000000e7");
    private static readonly Guid RunId = Guid.Parse("55555555-5555-7555-8555-555555555555");
    private static readonly DateTimeOffset SubmittedAt = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 14, 30, 0, TimeSpan.Zero);

    private readonly ITenantContext _tenant = Substitute.For<ITenantContext>();
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly IAdjudicationRepository _adjudication = Substitute.For<IAdjudicationRepository>();
    private readonly IReviewRepository _reviews = Substitute.For<IReviewRepository>();
    private readonly ICatalogRepository _catalog = Substitute.For<ICatalogRepository>();
    private readonly IServiceNetwork _network = Substitute.For<IServiceNetwork>();
    private readonly IRepairRequestService _repairs = Substitute.For<IRepairRequestService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IDecisionTrailWriter _trail = Substitute.For<IDecisionTrailWriter>();
    private readonly ISecurityEventWriter _securityEvents = Substitute.For<ISecurityEventWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly List<TrailRecord> _entries = [];
    private readonly ServiceCenterInfo _center = new(Guid.Parse("0199b000-0000-7000-8000-0000000000f1"), "Aurora Service North", Region.NA);
    private readonly Guid _repairRequestId = Guid.Parse("0199b000-0000-7000-8000-0000000000a1");
    private readonly Guid _notificationId = Guid.Parse("0199b000-0000-7000-8000-0000000000a2");

    public RecordReviewDecisionTests()
    {
        _tenant.IsResolved.Returns(true);
        _tenant.TenantId.Returns(Tenant);
        _tenant.PrincipalId.Returns(ReviewerSub);
        _tenant.PrincipalName.Returns(ReviewerName);
        _tenant.IsSystem.Returns(false);

        _unitOfWork.ExecuteInTransactionAsync(default!, default)
            .ReturnsForAnyArgs(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        _trail.WhenForAnyArgs(t => t.AppendAsync(default, default, default!, default!, default, default))
            .Do(call => _entries.Add(new TrailRecord(
                call.ArgAt<Guid>(0), call.ArgAt<TrailStep>(1), call.ArgAt<string>(2), call.ArgAt<string>(3),
                JsonSerializer.SerializeToElement(call.ArgAt<object?>(4)))));
        _catalog.GetProductAsync(ProductId, Arg.Any<CancellationToken>())
            .Returns(Product.Create(ProductId, Tenant, "AUR-TAB10", "Aurora Tab 10", "tablet", 349m, "EUR"));
        _network.FindServiceCenterAsync(Region.NA, "tablet", Arg.Any<CancellationToken>()).Returns(_center);
        _repairs.CreateAsync(default, default, default).ReturnsForAnyArgs(_repairRequestId);
        _notifications.EnqueueAsync(default, default!, default).ReturnsForAnyArgs(_notificationId);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Approve / Reject / RequestInformation ─────────────────────────────────────────────────

    [Fact]
    public async Task Approving_in_line_with_an_AI_approve_needs_no_justification_and_finalizes_by_the_reviewer()
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Approve));

        var result = await Execute(claim, ReviewDecisionKind.Approve, claimantExplanation: ApproveText);

        var recorded = result.ShouldBeOfType<RecordReviewDecisionResult.Recorded>();
        recorded.ClaimStatus.ShouldBe(ClaimStatus.Approved);
        recorded.Decision.OverridesAi.ShouldBeFalse();
        recorded.Decision.Justification.ShouldBeNull();
        recorded.Decision.ReviewerSub.ShouldBe(ReviewerSub);
        recorded.Decision.ReviewerName.ShouldBe(ReviewerName);
        recorded.Decision.RunId.ShouldBe(RunId);
        recorded.Decision.DecidedAt.ShouldBe(Now);

        claim.Status.ShouldBe(ClaimStatus.Approved);
        claim.FinalOutcome.ShouldBe(FinalOutcome.Approved);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.Reviewer);
        claim.FinalExplanation.ShouldBe(ApproveText);
        claim.FinalizedAt.ShouldBe(Now);
        _reviews.Received(1).Add(recorded.Decision);

        Received.InOrder(() =>
        {
            _repairs.CreateAsync(claim.Id, _center.Id, Arg.Any<CancellationToken>());
            _notifications.EnqueueAsync(claim.Id, ActionExecutor.ApprovedTemplate, Arg.Any<CancellationToken>());
        });
        _entries.Select(e => e.Step).ShouldBe([TrailStep.ReviewerDecided, TrailStep.ActionExecuted, TrailStep.ActionExecuted]);
        _entries.ShouldAllBe(e => e.ClaimId == claim.Id && e.Actor == ReviewerSub);
        _entries[1].Payload.GetProperty("action").GetString().ShouldBe("create_repair_request");
        _entries[2].Payload.GetProperty("action").GetString().ShouldBe("notify_customer");
        await _unitOfWork.ReceivedWithAnyArgs(1).ExecuteInTransactionAsync(default!, Ct);
    }

    [Fact]
    public async Task Rejecting_against_an_AI_approve_overrides_it_and_the_trail_keeps_the_justification_and_the_claimant_text()
    {
        var recommendation = Valid(AiDecision.Approve);
        var claim = ArrangeUnderReview(recommendation);

        var result = await Execute(claim, ReviewDecisionKind.Reject, Justification, RejectText);

        var recorded = result.ShouldBeOfType<RecordReviewDecisionResult.Recorded>();
        recorded.Decision.OverridesAi.ShouldBeTrue();
        recorded.Decision.Justification.ShouldBe(Justification);
        claim.Status.ShouldBe(ClaimStatus.Rejected);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.Reviewer);
        claim.FinalExplanation.ShouldBe(RejectText);
        claim.FinalExplanation!.ShouldNotContain(Justification);

        // The AI recommendation is advisory and stays as it was (FR-036).
        recommendation.Decision.ShouldBe(AiDecision.Approve);
        recommendation.ClaimantExplanation.ShouldBe(ApproveText);

        await _notifications.Received(1).EnqueueAsync(claim.Id, ActionExecutor.RejectedTemplate, Arg.Any<CancellationToken>());
        await _repairs.DidNotReceiveWithAnyArgs().CreateAsync(default, default, Ct);
        _entries.Select(e => e.Step).ShouldBe([TrailStep.ReviewerDecided, TrailStep.ActionExecuted]);

        var payload = _entries[0].Payload;
        payload.GetProperty("decisionId").GetGuid().ShouldBe(recorded.Decision.Id);
        payload.GetProperty("runId").GetGuid().ShouldBe(RunId);
        payload.GetProperty("decision").GetString().ShouldBe("Reject");
        payload.GetProperty("overridesAi").GetBoolean().ShouldBeTrue();
        payload.GetProperty("reviewer").GetString().ShouldBe(ReviewerName);
        payload.GetProperty("justification").GetString().ShouldBe(Justification);
        payload.GetProperty("claimantExplanation").GetString().ShouldBe(RejectText);
        _entries[0].Actor.ShouldBe(ReviewerSub);
    }

    [Fact]
    public async Task Requesting_information_pauses_the_claim_as_a_reviewer_request_without_counting_an_automatic_one()
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.HumanReview));

        var result = await Execute(
            claim, ReviewDecisionKind.RequestInformation,
            requestedItems: [new RequestedItem(" legible_invoice ", " The invoice total cannot be read. ")]);

        var recorded = result.ShouldBeOfType<RecordReviewDecisionResult.Recorded>();
        recorded.ClaimStatus.ShouldBe(ClaimStatus.PendingInformation);
        recorded.Decision.OverridesAi.ShouldBeFalse();
        claim.Status.ShouldBe(ClaimStatus.PendingInformation);
        claim.ReviewerInfoRequested.ShouldBeTrue();
        claim.AutoInfoRequestCount.ShouldBe(0);
        claim.RequestedItems.ShouldBe([new RequestedItem("LEGIBLE_INVOICE", "The invoice total cannot be read.")]);
        claim.FinalOutcome.ShouldBeNull();

        _entries.Select(e => e.Step).ShouldBe([TrailStep.ReviewerDecided]);
        _entries[0].Payload.GetProperty("requestedItems").EnumerateArray().Select(i => i.GetString()).ShouldBe(["LEGIBLE_INVOICE"]);
        _entries[0].Payload.GetProperty("claimantExplanation").ValueKind.ShouldBe(JsonValueKind.Null);
        ShouldNotHaveCalledIntegrations();
    }

    // ── overridesAi (FR-035) ───────────────────────────────────────────────────────────────────

    public static TheoryData<string> NonDecisiveRecommendations => new("HUMAN_REVIEW", "REQUEST_MORE_INFORMATION", "invalid", "missing");

    [Theory]
    [MemberData(nameof(NonDecisiveRecommendations))]
    public async Task Without_a_valid_approve_or_reject_recommendation_nothing_is_overridden_and_approval_needs_no_justification(string kind)
    {
        var claim = ArrangeUnderReview(RecommendationFor(kind));

        var result = await Execute(claim, ReviewDecisionKind.Approve, claimantExplanation: ApproveText);

        var recorded = result.ShouldBeOfType<RecordReviewDecisionResult.Recorded>();
        recorded.Decision.OverridesAi.ShouldBeFalse();
        claim.Status.ShouldBe(ClaimStatus.Approved);
    }

    [Theory]
    [MemberData(nameof(NonDecisiveRecommendations))]
    public async Task Rejecting_always_needs_a_justification_even_when_nothing_is_overridden(string kind)
    {
        var claim = ArrangeUnderReview(RecommendationFor(kind));

        var result = await Execute(claim, ReviewDecisionKind.Reject, claimantExplanation: RejectText);

        ShouldBeInvalid(result, "justification");
        ShouldBeUnchanged(claim);
    }

    [Fact]
    public async Task Approving_against_an_AI_reject_overrides_it_and_needs_a_justification()
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Reject));

        ShouldBeInvalid(await Execute(claim, ReviewDecisionKind.Approve, claimantExplanation: ApproveText), "justification");
        ShouldBeUnchanged(claim);

        var result = await Execute(claim, ReviewDecisionKind.Approve, Justification, ApproveText);
        result.ShouldBeOfType<RecordReviewDecisionResult.Recorded>().Decision.OverridesAi.ShouldBeTrue();
    }

    [Fact]
    public async Task Requesting_information_against_an_AI_approve_overrides_it_and_needs_a_justification()
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Approve));
        RequestedItem[] items = [new("PHOTO_OF_SERIAL_LABEL", "The serial label is not visible.")];

        ShouldBeInvalid(await Execute(claim, ReviewDecisionKind.RequestInformation, requestedItems: items), "justification");

        var result = await Execute(claim, ReviewDecisionKind.RequestInformation, Justification, requestedItems: items);
        result.ShouldBeOfType<RecordReviewDecisionResult.Recorded>().Decision.OverridesAi.ShouldBeTrue();
    }

    // ── Field rules (FR-035, FR-036) ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(9)]
    [InlineData(2001)]
    public async Task A_justification_outside_10_to_2000_characters_is_refused(int length)
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Approve));

        var result = await Execute(claim, ReviewDecisionKind.Reject, new string('j', length), RejectText);

        ShouldBeInvalid(result, "justification");
        ShouldBeUnchanged(claim);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(2000)]
    public async Task A_justification_of_10_to_2000_characters_is_accepted(int length)
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Approve));

        var result = await Execute(claim, ReviewDecisionKind.Reject, new string('j', length), RejectText);

        result.ShouldBeOfType<RecordReviewDecisionResult.Recorded>();
    }

    [Theory]
    [InlineData(ReviewDecisionKind.Approve, null)]
    [InlineData(ReviewDecisionKind.Approve, 19)]
    [InlineData(ReviewDecisionKind.Approve, 1501)]
    [InlineData(ReviewDecisionKind.Reject, null)]
    [InlineData(ReviewDecisionKind.Reject, 19)]
    [InlineData(ReviewDecisionKind.Reject, 1501)]
    public async Task Approve_and_reject_need_a_claimant_explanation_of_20_to_1500_characters(ReviewDecisionKind kind, int? length)
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.HumanReview));

        var result = await Execute(claim, kind, Justification, length is { } n ? new string('c', n) : null);

        ShouldBeInvalid(result, "claimantExplanation");
        ShouldBeUnchanged(claim);
    }

    [Theory]
    [InlineData("We found signs of fraud in the photos you sent us.", "fraud")]
    [InlineData("Your claim was declined under clause POL-1 of the policy.", "POL-1")]
    [InlineData("The photo EV-2 does not show the reported damage at all.", "EV-2")]
    [InlineData("Declined because of DUPLICATE_SERIAL_CLAIM on this unit.", "DUPLICATE_SERIAL_CLAIM")]
    [InlineData("The Risk assessment for this claim did not allow approval.", "Risk")]
    public async Task A_claimant_explanation_that_fails_the_screen_is_refused_naming_the_term(string text, string term)
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Reject));

        var result = await Execute(claim, ReviewDecisionKind.Reject, Justification, text);

        var message = ShouldBeInvalid(result, "claimantExplanation");
        message.ShouldContain($"\"{term}\"");
        ShouldBeUnchanged(claim);
    }

    [Fact]
    public async Task A_request_for_information_refuses_a_claimant_explanation()
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.HumanReview));

        var result = await Execute(
            claim, ReviewDecisionKind.RequestInformation, claimantExplanation: ApproveText,
            requestedItems: [new RequestedItem("INVOICE", "Please send the invoice.")]);

        ShouldBeInvalid(result, "claimantExplanation");
        ShouldBeUnchanged(claim);
    }

    [Fact]
    public async Task A_request_for_information_needs_requested_items()
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.HumanReview));

        ShouldBeInvalid(await Execute(claim, ReviewDecisionKind.RequestInformation), "requestedItems");
        ShouldBeInvalid(await Execute(claim, ReviewDecisionKind.RequestInformation, requestedItems: []), "requestedItems");
        ShouldBeInvalid(
            await Execute(claim, ReviewDecisionKind.RequestInformation, requestedItems: [new RequestedItem("INVOICE", " ")]),
            "requestedItems");
        ShouldBeUnchanged(claim);
    }

    [Fact]
    public async Task Requested_items_are_refused_on_an_approval()
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Approve));

        var result = await Execute(
            claim, ReviewDecisionKind.Approve, claimantExplanation: ApproveText,
            requestedItems: [new RequestedItem("INVOICE", "Please send the invoice.")]);

        ShouldBeInvalid(result, "requestedItems");
        ShouldBeUnchanged(claim);
    }

    // ── Separation of duties (research R29) ────────────────────────────────────────────────────

    [Fact]
    public async Task A_reviewer_who_submitted_the_claim_is_refused_with_a_security_event_and_nothing_changes()
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Approve), submittedBy: ReviewerSub);

        var result = await Execute(claim, ReviewDecisionKind.Approve, claimantExplanation: ApproveText);

        result.ShouldBeOfType<RecordReviewDecisionResult.SelfReviewRefused>()
            .Detail.ShouldBe("You submitted this claim; another reviewer must decide it");
        await _securityEvents.Received(1).RecordAsync(
            SecurityEventKind.SelfReviewRefused, ReviewerName, claim.Id.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
        ShouldBeUnchanged(claim);
    }

    [Fact]
    public async Task Self_review_is_refused_before_the_ETag_or_the_request_fields_are_looked_at()
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Approve), submittedBy: ReviewerSub);

        var result = await Execute(claim, ReviewDecisionKind.Reject, ifMatch: "\"1\"");

        result.ShouldBeOfType<RecordReviewDecisionResult.SelfReviewRefused>();
        await _securityEvents.ReceivedWithAnyArgs(1).RecordAsync(default, default!, default, default, Ct);
        ShouldBeUnchanged(claim);
    }

    [Fact]
    public async Task A_claim_submitted_by_another_agent_or_a_claimant_can_be_decided_without_a_security_event()
    {
        var agentClaim = ArrangeUnderReview(Valid(AiDecision.Approve));
        (await Execute(agentClaim, ReviewDecisionKind.Approve, claimantExplanation: ApproveText))
            .ShouldBeOfType<RecordReviewDecisionResult.Recorded>();

        _tenant.PrincipalId.Returns(Claim.ClaimantSubmitter);
        var claimantClaim = ArrangeUnderReview(Valid(AiDecision.Approve), submittedBy: Claim.ClaimantSubmitter);
        (await Execute(claimantClaim, ReviewDecisionKind.Approve, claimantExplanation: ApproveText))
            .ShouldBeOfType<RecordReviewDecisionResult.Recorded>();

        await _securityEvents.DidNotReceiveWithAnyArgs().RecordAsync(default, default!, default, default, Ct);
    }

    // ── Preconditions ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("W/\"4711\"")]
    [InlineData("\"4710\"")]
    [InlineData("not-a-version")]
    public async Task A_missing_or_stale_If_Match_fails_the_precondition(string? ifMatch)
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Approve));

        var result = await Execute(claim, ReviewDecisionKind.Approve, claimantExplanation: ApproveText, ifMatch: ifMatch);

        result.ShouldBeOfType<RecordReviewDecisionResult.PreconditionFailed>();
        ShouldBeUnchanged(claim);
    }

    [Theory]
    [InlineData("\"4711\"")]
    [InlineData("4711")]
    [InlineData(" \"4711\" ")]
    public async Task The_current_ETag_quoted_or_bare_passes_the_precondition(string ifMatch)
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Approve));

        var result = await Execute(claim, ReviewDecisionKind.Approve, claimantExplanation: ApproveText, ifMatch: ifMatch);

        result.ShouldBeOfType<RecordReviewDecisionResult.Recorded>();
    }

    [Fact]
    public async Task A_second_decision_with_the_old_ETag_fails_the_precondition_and_with_a_fresh_ETag_conflicts()
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Approve));
        (await Execute(claim, ReviewDecisionKind.Approve, claimantExplanation: ApproveText))
            .ShouldBeOfType<RecordReviewDecisionResult.Recorded>();
        _claims.GetRowVersionAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(RowVersion + 1);
        var entries = _entries.Count;

        (await Execute(claim, ReviewDecisionKind.Reject, Justification, RejectText))
            .ShouldBeOfType<RecordReviewDecisionResult.PreconditionFailed>();
        (await Execute(claim, ReviewDecisionKind.Reject, Justification, RejectText, ifMatch: ClaimQueries.FormatETag(RowVersion + 1)))
            .ShouldBeOfType<RecordReviewDecisionResult.Conflict>();

        claim.Status.ShouldBe(ClaimStatus.Approved);
        _entries.Count.ShouldBe(entries);
        _reviews.ReceivedWithAnyArgs(1).Add(default!);
    }

    [Fact]
    public async Task A_claim_that_is_not_under_review_conflicts()
    {
        var claim = NewClaim(SubmittingAgentSub);
        claim.StartEvaluation(SubmittedAt);
        Arrange(claim, Valid(AiDecision.Approve));

        var result = await Execute(claim, ReviewDecisionKind.Approve, claimantExplanation: ApproveText);

        result.ShouldBeOfType<RecordReviewDecisionResult.Conflict>();
        claim.Status.ShouldBe(ClaimStatus.UnderEvaluation);
        _entries.ShouldBeEmpty();
        _reviews.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Fact]
    public async Task A_concurrent_change_detected_on_save_conflicts()
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Approve));
        _unitOfWork.Configure().ExecuteInTransactionAsync(default!, Ct).ThrowsAsyncForAnyArgs(new ConcurrencyConflictException("changed"));

        var result = await Execute(claim, ReviewDecisionKind.Approve, claimantExplanation: ApproveText);

        result.ShouldBeOfType<RecordReviewDecisionResult.Conflict>().Detail.ShouldBe(RecordReviewDecision.ConcurrentChangeDetail);
    }

    [Fact]
    public async Task An_unknown_claim_or_a_claim_of_another_tenant_is_not_found()
    {
        (await Execute(Guid.NewGuid(), ReviewDecisionKind.Approve, null, ApproveText, null, ValidIfMatch))
            .ShouldBeOfType<RecordReviewDecisionResult.NotFound>();

        var foreign = NewClaim(SubmittingAgentSub, OtherTenant);
        _claims.GetAsync(foreign.Id, Arg.Any<CancellationToken>()).Returns(foreign);
        (await Execute(foreign.Id, ReviewDecisionKind.Approve, null, ApproveText, null, ValidIfMatch))
            .ShouldBeOfType<RecordReviewDecisionResult.NotFound>();

        _entries.ShouldBeEmpty();
        await _securityEvents.DidNotReceiveWithAnyArgs().RecordAsync(default, default!, default, default, Ct);
    }

    // ── ActionExecutor re-checks (fail closed) ─────────────────────────────────────────────────

    [Fact]
    public async Task The_executor_refuses_a_decision_for_another_tenant()
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Approve));
        var decision = Decision(claim, tenantId: OtherTenant);

        await Should.ThrowAsync<ActionRefusedException>(() => Executor().ExecuteReviewerDecisionAsync(decision, Ct));

        ShouldBeUnchanged(claim);
        await _unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!, Ct);
    }

    [Fact]
    public async Task The_executor_refuses_a_decision_by_the_submitter()
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Approve), submittedBy: ReviewerSub);

        await Should.ThrowAsync<ActionRefusedException>(() => Executor().ExecuteReviewerDecisionAsync(Decision(claim), Ct));

        ShouldBeUnchanged(claim);
    }

    [Fact]
    public async Task The_executor_refuses_a_decision_on_an_earlier_run()
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Approve));

        await Should.ThrowAsync<ActionRefusedException>(
            () => Executor().ExecuteReviewerDecisionAsync(Decision(claim, runId: Guid.NewGuid()), Ct));

        ShouldBeUnchanged(claim);
    }

    [Fact]
    public async Task The_executor_refuses_a_claimant_explanation_that_fails_the_screen()
    {
        var claim = ArrangeUnderReview(Valid(AiDecision.Approve));

        await Should.ThrowAsync<ActionRefusedException>(
            () => Executor().ExecuteReviewerDecisionAsync(Decision(claim, explanation: "Approved after the fraud review was cleared."), Ct));

        ShouldBeUnchanged(claim);
    }

    [Fact]
    public async Task The_executor_leaves_a_claim_that_is_not_under_review_to_the_state_machine()
    {
        var claim = NewClaim(SubmittingAgentSub);
        claim.StartEvaluation(SubmittedAt);
        Arrange(claim, Valid(AiDecision.Approve));

        await Should.ThrowAsync<InvalidClaimTransitionException>(() => Executor().ExecuteReviewerDecisionAsync(Decision(claim), Ct));

        claim.Status.ShouldBe(ClaimStatus.UnderEvaluation);
        _reviews.DidNotReceiveWithAnyArgs().Add(default!);
        _entries.ShouldBeEmpty();
    }

    // ── ETag and registration ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_claim_ETag_is_the_row_version_as_a_strong_tag()
    {
        ClaimQueries.FormatETag(4711).ShouldBe("\"4711\"");
        ClaimETag.TryParse(ClaimQueries.FormatETag(uint.MaxValue), out var max).ShouldBeTrue();
        max.ShouldBe(uint.MaxValue);
        ClaimETag.TryParse("\"-1\"", out _).ShouldBeFalse();
        ClaimETag.TryParse("\"4294967296\"", out _).ShouldBeFalse();
        ClaimETag.TryParse("\"\"", out _).ShouldBeFalse();
    }

    [Fact]
    public void The_application_registers_the_use_case_per_scope()
    {
        var services = new ServiceCollection();

        services.AddWarrantyApplication();

        services.Single(d => d.ServiceType == typeof(RecordReviewDecision)).Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private Task<RecordReviewDecisionResult> Execute(
        Claim claim,
        ReviewDecisionKind decision,
        string? justification = null,
        string? claimantExplanation = null,
        IReadOnlyList<RequestedItem>? requestedItems = null,
        string? ifMatch = ValidIfMatch)
        => Execute(claim.Id, decision, justification, claimantExplanation, requestedItems, ifMatch);

    private Task<RecordReviewDecisionResult> Execute(
        Guid claimId,
        ReviewDecisionKind decision,
        string? justification,
        string? claimantExplanation,
        IReadOnlyList<RequestedItem>? requestedItems,
        string? ifMatch)
        => new RecordReviewDecision(_tenant, _claims, _adjudication, _securityEvents, Executor(), _time)
            .ExecuteAsync(new RecordReviewDecisionCommand(claimId, ifMatch, decision, justification, claimantExplanation, requestedItems), Ct);

    private ActionExecutor Executor() => new(
        _tenant, _claims, _adjudication, _reviews, _catalog, _network, _repairs, _notifications, _trail, _unitOfWork, _time);

    private static Claim NewClaim(string submittedBy, Guid? tenantId = null)
    {
        var claimant = submittedBy == Claim.ClaimantSubmitter;
        return Claim.Submit(
            Guid.CreateVersion7(), tenantId ?? Tenant, ClaimReference.Generate(),
            claimant ? ClaimChannel.ClaimantPortal : ClaimChannel.AgentPortal, submittedBy, CustomerId,
            "claimant@synthetic-mail.test", null, "AUR-TAB10", ProductId, "SN-1001", new DateOnly(2026, 1, 15),
            "Brightline Electronics", 349m, Region.NA, "The tablet does not power on after charging overnight.", SubmittedAt);
    }

    /// <summary>A claim escalated to review behind the repository, with its run and <paramref name="recommendation"/>.</summary>
    private Claim ArrangeUnderReview(Recommendation? recommendation, string submittedBy = SubmittingAgentSub)
    {
        var claim = NewClaim(submittedBy);
        claim.StartEvaluation(SubmittedAt);
        claim.EscalateToReview(SubmittedAt);
        Arrange(claim, recommendation);
        return claim;
    }

    private void Arrange(Claim claim, Recommendation? recommendation)
    {
        var run = AdjudicationRun.Start(RunId, Tenant, claim.Id, claim.CurrentRound, "corr-t087", SubmittedAt);
        _claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);
        _claims.GetRowVersionAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(RowVersion);
        _adjudication.GetLatestRunAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(run);
        _adjudication.GetRunRecordAsync(RunId, Arg.Any<CancellationToken>())
            .Returns(new RunRecord(run, null, [], [], null, null, recommendation, null));
    }

    private static Recommendation Valid(AiDecision decision) => Recommendation.CreateValid(
        RunId, Tenant, "{}", decision,
        decision == AiDecision.Reject ? CoverageDetermination.NotCovered : CoverageDetermination.Covered, 90,
        "Staff-facing reasoning summary.", decision == AiDecision.Reject ? RejectText : ApproveText, [], [], [], false,
        "claude-opus-5-5", "decision", "v1");

    private static Recommendation? RecommendationFor(string kind) => kind switch
    {
        "HUMAN_REVIEW" => Valid(AiDecision.HumanReview),
        "REQUEST_MORE_INFORMATION" => Valid(AiDecision.RequestMoreInformation),
        "invalid" => Recommendation.CreateInvalid(
            RunId, Tenant, "{", ["$: not valid JSON"], "claude-opus-5-5", "decision", "v1", AiDecision.Approve, 95),
        _ => null,
    };

    private static ReviewDecision Decision(Claim claim, Guid? tenantId = null, Guid? runId = null, string explanation = ApproveText)
        => ReviewDecision.Create(
            Guid.CreateVersion7(), tenantId ?? Tenant, claim.Id, runId ?? RunId, ReviewerSub, ReviewerName, ReviewDecisionKind.Approve,
            null, explanation, null, null, Now);

    private static string ShouldBeInvalid(RecordReviewDecisionResult result, string field)
    {
        var invalid = result.ShouldBeOfType<RecordReviewDecisionResult.Invalid>();
        invalid.Errors.Keys.ShouldBe([field]);
        return invalid.Errors[field].ShouldHaveSingleItem();
    }

    private void ShouldBeUnchanged(Claim claim)
    {
        claim.Status.ShouldBe(ClaimStatus.UnderReview);
        claim.FinalOutcome.ShouldBeNull();
        claim.FinalExplanation.ShouldBeNull();
        claim.ReviewerInfoRequested.ShouldBeFalse();
        claim.UpdatedAt.ShouldBe(SubmittedAt);
        _reviews.DidNotReceiveWithAnyArgs().Add(default!);
        _entries.ShouldBeEmpty();
        ShouldNotHaveCalledIntegrations();
    }

    private void ShouldNotHaveCalledIntegrations()
        => (_network.ReceivedCalls().Count() + _repairs.ReceivedCalls().Count() + _notifications.ReceivedCalls().Count()).ShouldBe(0);

    private sealed record TrailRecord(Guid ClaimId, TrailStep Step, string Actor, string Summary, JsonElement Payload);
}
