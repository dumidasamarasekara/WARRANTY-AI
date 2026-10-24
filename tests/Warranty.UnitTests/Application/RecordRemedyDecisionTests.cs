using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Warranty.Application;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Actions;
using Warranty.Application.Fulfillment;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Remedies;

namespace Warranty.UnitTests.Application;

/// <summary>
/// Reviewer remedy decisions (002 T221; FR-002, FR-011, FR-013, FR-015, research R4): <see cref="RecordRemedyDecision"/>
/// refuses a remedy outside the remedy's allowed list (400 naming them), a change without a reason (400), self-review
/// (403 + <c>SELF_REVIEW_REFUSED</c>), a missing or stale remedy ETag (412) and a remedy that cannot be decided now
/// (409) — all before the <see cref="IActionExecutor"/> is called. Accepted decisions go through the executor's
/// <c>ExecuteRemedyDecisionAsync</c> (T227), which applies the remedy, its side effects and the trail entry.
/// </summary>
public sealed class RecordRemedyDecisionTests
{
    private const string ReviewerSub = "reviewer-sub-t221";
    private const string ReviewerName = "Aurora Reviewer";
    private const string SubmittingAgentSub = "agent-sub-t221";
    private const uint RowVersion = 5150;
    private const uint ClaimRowVersion = 9001;
    private const string ValidIfMatch = "\"5150\"";
    private const string Reason = "The customer prefers a refund; replacement stock is unavailable.";
    private const decimal ClaimValue = 349m;

    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OtherTenant = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid CustomerId = Guid.Parse("0199b000-0000-7000-8000-0000000003d7");
    private static readonly Guid ProductId = Guid.Parse("0199b000-0000-7000-8000-0000000003e7");
    private static readonly Guid PolicyId = Guid.Parse("0199b000-0000-7000-8000-0000000003b1");
    private static readonly Guid VersionId = Guid.Parse("0199b000-0000-7000-8000-0000000003b2");
    private static readonly DateTimeOffset SubmittedAt = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ApprovedAt = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 14, 30, 0, TimeSpan.Zero);

    /// <summary>A policy choice between Refund and Replacement for a covered defect; Replacement after two failed repairs.</summary>
    private static readonly RemedyRules ChoiceRules = new(
        CoveredDefect: new RemedyChoice(null, [RemedyType.Refund, RemedyType.Replacement]),
        RepairNotEconomical: null,
        RepairFailed: new RepairFailedRule(2, RemedyType.Replacement, null),
        ProtectionPlan: null);

    private readonly ITenantContext _tenant = Substitute.For<ITenantContext>();
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly IRemedyRepository _remedies = Substitute.For<IRemedyRepository>();
    private readonly IRepairRepository _repairs = Substitute.For<IRepairRepository>();
    private readonly ISecurityEventWriter _securityEvents = Substitute.For<ISecurityEventWriter>();
    private readonly IActionExecutor _actions = Substitute.For<IActionExecutor>();

    // The real executor's ports (T225/T227).
    private readonly IAdjudicationRepository _adjudication = Substitute.For<IAdjudicationRepository>();
    private readonly IReviewRepository _reviews = Substitute.For<IReviewRepository>();
    private readonly ICatalogRepository _catalog = Substitute.For<ICatalogRepository>();
    private readonly IPolicyRepository _policies = Substitute.For<IPolicyRepository>();
    private readonly IServiceNetwork _network = Substitute.For<IServiceNetwork>();
    private readonly IRefundService _refunds = Substitute.For<IRefundService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IDecisionTrailWriter _trail = Substitute.For<IDecisionTrailWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly List<TrailRecord> _entries = [];

    public RecordRemedyDecisionTests()
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
                call.ArgAt<TrailStep>(1), call.ArgAt<string>(2), JsonSerializer.SerializeToElement(call.ArgAt<object?>(4)))));
        _catalog.GetProductAsync(ProductId, Arg.Any<CancellationToken>())
            .Returns(Product.Create(ProductId, Tenant, "AUR-TAB10", "Aurora Tab 10", "tablet", ClaimValue, "USD"));
        _refunds.RequestRefundAsync(default, default, default).ReturnsForAnyArgs(Guid.Parse("0199b000-0000-7000-8000-0000000003a3"));

        var terms = new CoverageTerms(
            new Dictionary<Region, int> { [Region.NA] = 12, [Region.EU] = 24 }, new Dictionary<string, int>(),
            AccidentalDamageTerms.NotCovered, [ExclusionCode.AccidentalDamage, ExclusionCode.LiquidDamage]);
        var version = PolicyVersion.Create(
            VersionId, Tenant, PolicyId, 2, new DateOnly(2025, 1, 1), null, [Region.NA, Region.EU], [], terms,
            "tenant-aurora/policies/AUR-WP-v2.md", "checksum", ChoiceRules);
        _policies.GetVersionsAsync(Arg.Any<CancellationToken>()).Returns([version]);
        _policies.GetVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(version);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Allowed remedies (FR-002) ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_remedy_outside_the_allowed_list_is_refused_naming_the_allowed_remedies()
    {
        var (claim, remedy) = ArrangeChoice();

        var result = await Execute(claim, RemedyType.Repair);

        var message = ShouldBeInvalid(result, "remedy");
        message.ShouldContain("Refund");
        message.ShouldContain("Replacement");
        ShouldBeUnchanged(remedy);
    }

    [Fact]
    public async Task A_change_after_the_repair_limit_must_name_a_remedy_of_the_new_evaluation()
    {
        var (claim, remedy) = ArrangeRepairFailedLimit();

        var result = await Execute(claim, RemedyType.Repair, Reason);

        ShouldBeInvalid(result, "remedy").ShouldContain("Replacement");
        ShouldBeUnchanged(remedy);
    }

    // ── Reason rule (FR-015) ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_first_decision_of_a_policy_choice_needs_no_reason_and_goes_through_the_executor()
    {
        var (claim, _) = ArrangeChoice();

        var result = await Execute(claim, RemedyType.Refund);

        result.ShouldBeOfType<RecordRemedyDecisionResult.Recorded>();
        _actions.ReceivedCalls().ShouldHaveSingleItem().GetMethodInfo().Name.ShouldBe("ExecuteRemedyDecisionAsync");
    }

    public static TheoryData<string, string?> ChangesWithoutAValidReason => new()
    {
        { "changeInProgress", null },
        { "changeInProgress", "   " },
        { "changeInProgress", new string('r', RemedyDecision.MinReasonLength - 1) },
        { "changeInProgress", new string('r', RemedyDecision.MaxReasonLength + 1) },
        { "afterRepairLimit", null },
        { "afterRepairLimit", new string('r', RemedyDecision.MinReasonLength - 1) },
    };

    [Theory]
    [MemberData(nameof(ChangesWithoutAValidReason))]
    public async Task Every_change_after_the_first_remedy_needs_a_reason_of_10_to_1000_characters(string arrangement, string? reason)
    {
        var (claim, remedy, target) = ArrangeChange(arrangement);

        var result = await Execute(claim, target, reason);

        ShouldBeInvalid(result, "reason");
        ShouldBeUnchanged(remedy);
    }

    [Theory]
    [InlineData("changeInProgress", RemedyDecision.MinReasonLength)]
    [InlineData("changeInProgress", RemedyDecision.MaxReasonLength)]
    [InlineData("afterRepairLimit", RemedyDecision.MinReasonLength)]
    public async Task A_change_with_a_reason_of_10_to_1000_characters_goes_through_the_executor(string arrangement, int length)
    {
        var (claim, _, target) = ArrangeChange(arrangement);

        var result = await Execute(claim, target, new string('r', length));

        result.ShouldBeOfType<RecordRemedyDecisionResult.Recorded>();
        _actions.ReceivedCalls().ShouldHaveSingleItem().GetMethodInfo().Name.ShouldBe("ExecuteRemedyDecisionAsync");
    }

    // ── Separation of duties (001 research R29) ────────────────────────────────────────────────

    [Fact]
    public async Task A_reviewer_who_submitted_the_claim_is_refused_with_a_SELF_REVIEW_REFUSED_event()
    {
        var (claim, remedy) = ArrangeChoice(submittedBy: ReviewerSub);

        var result = await Execute(claim, RemedyType.Refund);

        result.ShouldBeOfType<RecordRemedyDecisionResult.SelfReviewRefused>().Detail.ShouldBe(RecordRemedyDecision.SelfReviewDetail);
        await _securityEvents.Received(1).RecordAsync(
            SecurityEventKind.SelfReviewRefused, ReviewerName, claim.Id.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
        ShouldBeUnchanged(remedy);
    }

    [Fact]
    public async Task Self_review_is_refused_before_the_ETag_or_the_request_fields_are_looked_at()
    {
        var (claim, remedy) = ArrangeChoice(submittedBy: ReviewerSub);

        var result = await Execute(claim, RemedyType.Repair, reason: null, ifMatch: "\"1\"");

        result.ShouldBeOfType<RecordRemedyDecisionResult.SelfReviewRefused>();
        await _securityEvents.ReceivedWithAnyArgs(1).RecordAsync(default, default!, default, default, Ct);
        ShouldBeUnchanged(remedy);
    }

    // ── Preconditions ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("W/\"5150\"")]
    [InlineData("\"5149\"")]
    [InlineData("not-a-version")]
    public async Task A_missing_or_stale_If_Match_fails_the_precondition(string? ifMatch)
    {
        var (claim, remedy) = ArrangeChoice();

        var result = await Execute(claim, RemedyType.Refund, ifMatch: ifMatch);

        result.ShouldBeOfType<RecordRemedyDecisionResult.PreconditionFailed>();
        ShouldBeUnchanged(remedy);
    }

    [Fact]
    public async Task The_claim_ETag_is_not_the_remedy_ETag()
    {
        var (claim, remedy) = ArrangeChoice();

        var result = await Execute(claim, RemedyType.Refund, ifMatch: $"\"{ClaimRowVersion}\"");

        result.ShouldBeOfType<RecordRemedyDecisionResult.PreconditionFailed>();
        ShouldBeUnchanged(remedy);
    }

    [Fact]
    public async Task A_completed_remedy_is_not_awaiting_a_decision_and_conflicts()
    {
        var (claim, remedy, _) = ArrangeChange("changeInProgress");
        remedy.Complete(RemedyType.Replacement, Now);

        var result = await Execute(claim, RemedyType.Refund, Reason);

        result.ShouldBeOfType<RecordRemedyDecisionResult.Conflict>();
        remedy.State.ShouldBe(RemedyState.Completed);
        _actions.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_claim_without_a_remedy_conflicts()
    {
        var claim = NewClaim(SubmittingAgentSub);
        claim.StartEvaluation(SubmittedAt);
        claim.EscalateToReview(SubmittedAt);
        _claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);
        _claims.GetRowVersionAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(ClaimRowVersion);

        var result = await Execute(claim, RemedyType.Refund);

        result.ShouldBeOfType<RecordRemedyDecisionResult.Conflict>();
        _actions.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_claim_or_a_claim_of_another_tenant_is_not_found()
    {
        (await UseCase().ExecuteAsync(new RecordRemedyDecisionCommand(Guid.NewGuid(), ValidIfMatch, RemedyType.Refund, null), Ct))
            .ShouldBeOfType<RecordRemedyDecisionResult.NotFound>();

        var foreign = NewClaim(SubmittingAgentSub, OtherTenant);
        _claims.GetAsync(foreign.Id, Arg.Any<CancellationToken>()).Returns(foreign);
        (await Execute(foreign, RemedyType.Refund)).ShouldBeOfType<RecordRemedyDecisionResult.NotFound>();

        _actions.ReceivedCalls().ShouldBeEmpty();
        await _securityEvents.DidNotReceiveWithAnyArgs().RecordAsync(default, default!, default, default, Ct);
    }

    // ── Applied through the real ActionExecutor (T227) ─────────────────────────────────────────

    [Fact]
    public async Task Deciding_Refund_from_a_policy_choice_sets_it_by_the_reviewer_and_requests_the_refund_once()
    {
        var (claim, remedy) = ArrangeChoice();
        var decisions = new List<RemedyDecision>();
        _remedies.WhenForAnyArgs(r => r.AddDecision(default!)).Do(call => decisions.Add(call.Arg<RemedyDecision>()));

        var result = await Execute(claim, RemedyType.Refund, executor: RealExecutor());

        result.ShouldBeOfType<RecordRemedyDecisionResult.Recorded>().Remedy.ShouldBeSameAs(remedy);
        remedy.State.ShouldBe(RemedyState.InProgress);
        remedy.Remedy.ShouldBe(RemedyType.Refund);
        claim.Status.ShouldBe(ClaimStatus.Approved);
        claim.FinalOutcome.ShouldBe(FinalOutcome.Approved);

        var decided = decisions.ShouldHaveSingleItem();
        (decided.Kind, decided.Remedy, decided.DecidedBy, decided.Actor, decided.Reason, decided.Seq).ShouldBe(
            (RemedyDecisionKind.Decided, RemedyType.Refund, RemedyDecidedBy.Reviewer, ReviewerSub, null, 2));
        await _refunds.Received(1).RequestRefundAsync(claim.Id, ClaimValue, Arg.Any<CancellationToken>());
        _refunds.ReceivedCalls().Count().ShouldBe(1);
        _entries.Select(e => e.Step).ShouldContain(TrailStep.RemedyDecided);
        _entries.ShouldAllBe(e => e.Actor == ReviewerSub);
        await _unitOfWork.ReceivedWithAnyArgs(1).ExecuteInTransactionAsync(default!, Ct);
    }

    [Fact]
    public async Task A_change_keeps_the_reviewers_reason_in_the_decision_and_the_trail()
    {
        var (claim, remedy, target) = ArrangeChange("changeInProgress");
        var decisions = new List<RemedyDecision>();
        _remedies.WhenForAnyArgs(r => r.AddDecision(default!)).Do(call => decisions.Add(call.Arg<RemedyDecision>()));

        var result = await Execute(claim, target, Reason, executor: RealExecutor());

        result.ShouldBeOfType<RecordRemedyDecisionResult.Recorded>();
        remedy.Remedy.ShouldBe(target);
        remedy.State.ShouldBe(RemedyState.InProgress);
        var decided = decisions.Where(d => d.Kind == RemedyDecisionKind.Decided).ShouldHaveSingleItem();
        decided.Reason.ShouldBe(Reason);
        decided.Actor.ShouldBe(ReviewerSub);
        _entries.Single(e => e.Step == TrailStep.RemedyDecided).Payload.GetProperty("reason").GetString().ShouldBe(Reason);
    }

    [Fact]
    public async Task After_the_repair_limit_the_reviewer_confirms_the_single_recommended_remedy_with_a_reason()
    {
        // FR-031: a single remedy of the repairFailed row is not applied automatically; the reviewer confirms it.
        var (claim, remedy) = ArrangeRepairFailedLimit();
        var decisions = new List<RemedyDecision>();
        _remedies.WhenForAnyArgs(r => r.AddDecision(default!)).Do(call => decisions.Add(call.Arg<RemedyDecision>()));

        var result = await Execute(claim, RemedyType.Replacement, Reason, executor: RealExecutor());

        result.ShouldBeOfType<RecordRemedyDecisionResult.Recorded>();
        (remedy.State, remedy.Remedy, remedy.Scenario).ShouldBe((RemedyState.InProgress, RemedyType.Replacement, RemedyScenario.RepairFailed));
        var decided = decisions.ShouldHaveSingleItem();
        (decided.Seq, decided.Kind, decided.TableRow, decided.DecidedBy, decided.Reason).ShouldBe(
            (4, RemedyDecisionKind.Decided, "repairFailed", RemedyDecidedBy.Reviewer, Reason));
        _refunds.ReceivedCalls().ShouldBeEmpty();
        claim.Status.ShouldBe(ClaimStatus.Approved);
    }

    [Fact]
    public void The_application_registers_the_use_case_per_scope()
    {
        var services = new ServiceCollection();

        services.AddWarrantyApplication();

        services.Single(d => d.ServiceType == typeof(RecordRemedyDecision)).Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private Task<RecordRemedyDecisionResult> Execute(
        Claim claim,
        RemedyType remedy,
        string? reason = null,
        string? ifMatch = ValidIfMatch,
        IActionExecutor? executor = null)
        => UseCase(executor).ExecuteAsync(new RecordRemedyDecisionCommand(claim.Id, ifMatch, remedy, reason), Ct);

    private RecordRemedyDecision UseCase(IActionExecutor? executor = null)
        => new(_tenant, _claims, _remedies, _repairs, _securityEvents, executor ?? _actions);

    /// <summary>The real executor with the remedy ports (T225, T227).</summary>
    private ActionExecutor RealExecutor() => new(
        _tenant, _claims, _adjudication, _reviews, _catalog, _policies, _network, _remedies, _repairs, _refunds, _notifications,
        _trail, _unitOfWork, new RemedyPolicyEvaluator(), _time);

    private static Claim NewClaim(string submittedBy, Guid? tenantId = null) => Claim.Submit(
        Guid.CreateVersion7(), tenantId ?? Tenant, ClaimReference.Generate(), ClaimChannel.AgentPortal, submittedBy, CustomerId,
        "claimant@synthetic-mail.test", null, "AUR-TAB10", ProductId, "SN-1001", new DateOnly(2026, 1, 15),
        "Aurora Store", ClaimValue, Region.NA, "The tablet does not power on after charging overnight.", SubmittedAt);

    /// <summary>A claim approved by a reviewer, with <paramref name="remedy"/> and its decision history behind the repositories.</summary>
    private Claim ArrangeApproved(ClaimRemedy remedy, IReadOnlyList<RemedyDecision> decisions, Claim claim)
    {
        claim.StartEvaluation(SubmittedAt);
        claim.EscalateToReview(SubmittedAt);
        claim.FinalizeApproved("Your tablet is covered for manufacturing defects.", DecidedBy.Reviewer, ApprovedAt);

        _claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);
        _claims.GetRowVersionAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(ClaimRowVersion);
        _remedies.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(remedy);
        _remedies.GetRowVersionAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(RowVersion);
        _remedies.GetDecisionsAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(decisions);
        return claim;
    }

    /// <summary>A policy choice (Refund or Replacement) awaiting its first decision.</summary>
    private (Claim Claim, ClaimRemedy Remedy) ArrangeChoice(string submittedBy = SubmittingAgentSub)
    {
        var claim = NewClaim(submittedBy);
        var remedy = ClaimRemedy.AwaitDecision(
            Guid.CreateVersion7(), Tenant, claim.Id, VersionId, RemedyScenario.CoveredDefect,
            [RemedyType.Refund, RemedyType.Replacement], RemedyPendingReason.PolicyChoice, ApprovedAt);
        ArrangeApproved(remedy, [Evaluated(claim, 1, RemedyScenario.CoveredDefect, [RemedyType.Refund, RemedyType.Replacement])], claim);
        return (claim, remedy);
    }

    /// <summary>
    /// <c>changeInProgress</c>: the choice was decided as Replacement (in progress, not completed) and is changed to
    /// Refund. <c>afterRepairLimit</c>: Repair set by policy failed twice; the remedy awaits a decision (Replacement).
    /// </summary>
    private (Claim Claim, ClaimRemedy Remedy, RemedyType Target) ArrangeChange(string arrangement)
    {
        if (arrangement == "changeInProgress")
        {
            var claim = NewClaim(SubmittingAgentSub);
            RemedyType[] allowed = [RemedyType.Refund, RemedyType.Replacement];
            var remedy = ClaimRemedy.AwaitDecision(
                Guid.CreateVersion7(), Tenant, claim.Id, VersionId, RemedyScenario.CoveredDefect, allowed,
                RemedyPendingReason.PolicyChoice, ApprovedAt);
            remedy.Decide(RemedyType.Replacement, ApprovedAt);
            ArrangeApproved(remedy, [Evaluated(claim, 1, RemedyScenario.CoveredDefect, allowed),
                ReviewerDecided(claim, 2, RemedyScenario.CoveredDefect, allowed, RemedyType.Replacement)], claim);
            return (claim, remedy, RemedyType.Refund);
        }

        var (repairClaim, repairRemedy) = ArrangeRepairFailedLimit();
        return (repairClaim, repairRemedy, RemedyType.Replacement);
    }

    private (Claim Claim, ClaimRemedy Remedy) ArrangeRepairFailedLimit()
    {
        var claim = NewClaim(SubmittingAgentSub);
        var remedy = ClaimRemedy.SetByPolicy(
            Guid.CreateVersion7(), Tenant, claim.Id, VersionId, RemedyScenario.CoveredDefect, RemedyType.Repair, [RemedyType.Repair],
            ApprovedAt);
        remedy.ReturnToDecision(VersionId, RemedyScenario.RepairFailed, [RemedyType.Replacement], RemedyPendingReason.RepairFailedLimit, ApprovedAt.AddDays(1));
        ArrangeApproved(remedy,
        [
            Evaluated(claim, 1, RemedyScenario.CoveredDefect, [RemedyType.Repair]),
            RemedyDecision.Decided(
                Guid.CreateVersion7(), Tenant, claim.Id, 2, RemedyScenario.CoveredDefect, VersionId, "coveredDefect", [RemedyType.Repair],
                RemedyType.Repair, RemedyDecidedBy.Policy, RemedyDecision.SystemActor, null, 0, null, ApprovedAt),
            Evaluated(claim, 3, RemedyScenario.RepairFailed, [RemedyType.Replacement], failedAttempts: 2),
        ], claim);
        return (claim, remedy);
    }

    private static RemedyDecision Evaluated(
        Claim claim, int seq, RemedyScenario scenario, IReadOnlyList<RemedyType> allowed, int failedAttempts = 0)
        => RemedyDecision.Evaluated(
            Guid.CreateVersion7(), Tenant, claim.Id, seq, scenario, VersionId, RemedyPolicyEvaluator.TableRowOf(scenario), allowed,
            failedAttempts, null, ApprovedAt);

    private static RemedyDecision ReviewerDecided(
        Claim claim, int seq, RemedyScenario scenario, IReadOnlyList<RemedyType> allowed, RemedyType remedy)
        => RemedyDecision.Decided(
            Guid.CreateVersion7(), Tenant, claim.Id, seq, scenario, VersionId, RemedyPolicyEvaluator.TableRowOf(scenario), allowed,
            remedy, RemedyDecidedBy.Reviewer, "another-reviewer-sub", null, 0, null, ApprovedAt);

    private static string ShouldBeInvalid(RecordRemedyDecisionResult result, string field)
    {
        var invalid = result.ShouldBeOfType<RecordRemedyDecisionResult.Invalid>();
        invalid.Errors.Keys.ShouldBe([field]);
        return invalid.Errors[field].ShouldHaveSingleItem();
    }

    private void ShouldBeUnchanged(ClaimRemedy remedy)
    {
        remedy.UpdatedAt.ShouldBeLessThan(Now, "a decision would update the remedy at the current time");
        _actions.ReceivedCalls().ShouldBeEmpty();
        _remedies.DidNotReceiveWithAnyArgs().AddDecision(default!);
        _entries.ShouldBeEmpty();
        _refunds.ReceivedCalls().ShouldBeEmpty();
    }

    private sealed record TrailRecord(TrailStep Step, string Actor, JsonElement Payload);
}
