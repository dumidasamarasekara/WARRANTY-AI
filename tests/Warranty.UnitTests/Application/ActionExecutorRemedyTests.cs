using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Actions;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Domain.Review;
using Warranty.Guardrails;
using Warranty.Guardrails.Remedies;
using Warranty.UnitTests.Domain;
using Warranty.UnitTests.Guardrails;
using Warranty.UnitTests.Infrastructure;

namespace Warranty.UnitTests.Application;

/// <summary>
/// The remedy set at approval (002 research R3, FR-009 – FR-011): automatic and reviewer approvals evaluate the
/// applicable policy version's remedy rules in the approval transaction. One remedy is set by policy (Repair
/// creates a <see cref="Repair"/> with the suggested centre instead of a 001 repair request; Refund calls the
/// refund port once); a choice or a missing rule leaves the claim Approved with the remedy awaiting a reviewer.
/// </summary>
public sealed class ActionExecutorRemedyTests
{
    private static readonly Guid Tenant = GuardrailScenario.Tenant;
    private static readonly Guid CustomerId = Guid.Parse("0199b000-0000-7000-8000-0000000002d7");
    private static readonly Guid ProductId = Guid.Parse("0199b000-0000-7000-8000-0000000002e7");
    private static readonly Guid PolicyId = Guid.Parse("0199b000-0000-7000-8000-0000000002b1");
    private static readonly Guid VersionId = Guid.Parse("0199b000-0000-7000-8000-0000000002b2");
    private static readonly Guid RunId = Guid.Parse("0199b000-0000-7000-8000-0000000002c1");
    private static readonly DateTimeOffset SubmittedAt = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 5, 0, TimeSpan.Zero);
    private const decimal ClaimValue = 349m;

    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly IAdjudicationRepository _adjudication = Substitute.For<IAdjudicationRepository>();
    private readonly IReviewRepository _reviews = Substitute.For<IReviewRepository>();
    private readonly ICatalogRepository _catalog = Substitute.For<ICatalogRepository>();
    private readonly IPolicyRepository _policies = Substitute.For<IPolicyRepository>();
    private readonly IServiceNetwork _network = Substitute.For<IServiceNetwork>();
    private readonly IRepairRequestService _repairRequests = Substitute.For<IRepairRequestService>();
    private readonly IRemedyRepository _remedies = Substitute.For<IRemedyRepository>();
    private readonly IRepairRepository _repairs = Substitute.For<IRepairRepository>();
    private readonly IRefundService _refunds = Substitute.For<IRefundService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IDecisionTrailWriter _trail = Substitute.For<IDecisionTrailWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _time = new(Now);

    private readonly List<TrailRecord> _entries = [];
    private readonly List<(object Row, bool InTransaction)> _writes = [];
    private bool _inTransaction;

    private readonly EligibleServiceCenter _otherCategory = new(
        Guid.Parse("0199b000-0000-7000-8000-0000000002f0"), "Aurora Appliances", Region.NA, "1 Example Road", "Mon–Fri", null, 5, ServesClaim: false);
    private readonly EligibleServiceCenter _suggested = new(
        Guid.Parse("0199b000-0000-7000-8000-0000000002f1"), "Aurora Service Center Springfield", Region.NA, "200 Example Boulevard", "Mon–Fri", null, 10, ServesClaim: true);
    private readonly EligibleServiceCenter _second = new(
        Guid.Parse("0199b000-0000-7000-8000-0000000002f2"), "Aurora Service Center Riverside", Region.NA, "45 Sample Street", "Mon–Fri", null, 20, ServesClaim: true);

    public ActionExecutorRemedyTests()
    {
        _unitOfWork.ExecuteInTransactionAsync(default!, default)
            .ReturnsForAnyArgs(async call =>
            {
                _inTransaction = true;
                try
                {
                    await call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>());
                }
                finally
                {
                    _inTransaction = false;
                }
            });
        _trail.WhenForAnyArgs(t => t.AppendAsync(default, default, default!, default!, default, default))
            .Do(call => _entries.Add(new TrailRecord(
                call.ArgAt<TrailStep>(1), call.ArgAt<string>(2), JsonSerializer.SerializeToElement(call.ArgAt<object?>(4)))));
        _remedies.WhenForAnyArgs(r => r.Add(default!)).Do(call => _writes.Add((call.Arg<ClaimRemedy>(), _inTransaction)));
        _remedies.WhenForAnyArgs(r => r.AddDecision(default!)).Do(call => _writes.Add((call.Arg<RemedyDecision>(), _inTransaction)));
        _repairs.WhenForAnyArgs(r => r.Add(default!)).Do(call => _writes.Add((call.Arg<Repair>(), _inTransaction)));

        _catalog.GetProductAsync(ProductId, Arg.Any<CancellationToken>())
            .Returns(Product.Create(ProductId, Tenant, "AUR-TAB10", "Aurora Tab 10", "tablet", ClaimValue, "USD"));
        _network.ListEligibleAsync(Region.NA, "tablet", ServiceCenterPurpose.Repair, Arg.Any<CancellationToken>()).Returns([_otherCategory, _suggested, _second]);
        _notifications.EnqueueAsync(default, default!, default).ReturnsForAnyArgs(Guid.Parse("0199b000-0000-7000-8000-0000000002a2"));
        _refunds.RequestRefundAsync(default, default, default).ReturnsForAnyArgs(Guid.Parse("0199b000-0000-7000-8000-0000000002a3"));
        UseRules(RemedyRulesContract.Aurora);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IEnumerable<ClaimRemedy> AddedRemedies => _writes.Select(w => w.Row).OfType<ClaimRemedy>();

    private IEnumerable<RemedyDecision> AddedDecisions => _writes.Select(w => w.Row).OfType<RemedyDecision>();

    private IEnumerable<Repair> AddedRepairs => _writes.Select(w => w.Row).OfType<Repair>();

    // ── Remedy set by policy ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_automatic_approval_sets_Repair_by_policy_in_the_approval_transaction()
    {
        var claim = ArrangeAutomaticApproval(out var action);

        (await Executor().ExecuteAsync(action, Ct)).ShouldBe(ActionExecution.Executed);

        claim.Status.ShouldBe(ClaimStatus.Approved);
        var remedy = AddedRemedies.ShouldHaveSingleItem();
        remedy.ClaimId.ShouldBe(claim.Id);
        remedy.TenantId.ShouldBe(Tenant);
        remedy.State.ShouldBe(RemedyState.InProgress);
        remedy.Remedy.ShouldBe(RemedyType.Repair);
        remedy.PolicyVersionId.ShouldBe(VersionId);
        remedy.Scenario.ShouldBe(RemedyScenario.CoveredDefect);

        AddedDecisions.Select(d => (d.Seq, d.Kind)).ShouldBe([(1, RemedyDecisionKind.Evaluated), (2, RemedyDecisionKind.Decided)]);
        AddedDecisions.ShouldAllBe(d => d.PolicyVersionId == VersionId && d.TableRow == "coveredDefect");
        var decided = AddedDecisions.Single(d => d.Kind == RemedyDecisionKind.Decided);
        (decided.Remedy, decided.DecidedBy, decided.Actor, decided.Reason).ShouldBe(
            (RemedyType.Repair, RemedyDecidedBy.Policy, RemedyDecision.SystemActor, null));

        _writes.ShouldNotBeEmpty();
        _writes.ShouldAllBe(w => w.InTransaction, "the remedy is written in the approval transaction");
        await _unitOfWork.ReceivedWithAnyArgs(1).ExecuteInTransactionAsync(default!, Ct);
    }

    [Fact]
    public async Task Repair_creates_a_repair_at_the_first_centre_serving_the_claim_and_no_001_repair_request()
    {
        var claim = ArrangeAutomaticApproval(out var action);

        await Executor().ExecuteAsync(action, Ct);

        var repair = AddedRepairs.ShouldHaveSingleItem();
        repair.ClaimId.ShouldBe(claim.Id);
        repair.Status.ShouldBe(RepairStatus.AwaitingHandover);
        repair.ActiveHandover.ShouldNotBeNull().ServiceCenterId.ShouldBe(_suggested.Id);
        repair.ActiveHandover!.Assignment.ShouldBe(HandoverAssignment.Suggested);
        repair.Events.Select(e => e.Type).ShouldBe([RepairEventType.CentreSuggested]);

        await _repairRequests.DidNotReceiveWithAnyArgs().CreateAsync(default, default, Ct);
        await _network.DidNotReceiveWithAnyArgs().FindServiceCenterAsync(default, default!, Ct);
        await _refunds.DidNotReceiveWithAnyArgs().RequestRefundAsync(default, default, Ct);
    }

    [Fact]
    public async Task Without_a_centre_serving_the_claim_the_repair_waits_for_staff_to_choose_one()
    {
        _network.ListEligibleAsync(Region.NA, "tablet", ServiceCenterPurpose.Repair, Arg.Any<CancellationToken>()).Returns([_otherCategory]);
        ArrangeAutomaticApproval(out var action);

        await Executor().ExecuteAsync(action, Ct);

        var repair = AddedRepairs.ShouldHaveSingleItem();
        repair.Status.ShouldBe(RepairStatus.AwaitingHandover);
        repair.ActiveHandover.ShouldBeNull();
        repair.Events.ShouldBeEmpty();
        _entries.Select(e => e.Step).ShouldNotContain(TrailStep.RepairCentreAssigned);
    }

    [Fact]
    public async Task The_trail_records_the_policy_basis_of_the_remedy_and_the_suggested_centre_and_keeps_the_approval_notification()
    {
        var claim = ArrangeAutomaticApproval(out var action);

        await Executor().ExecuteAsync(action, Ct);

        _entries.Select(e => e.Step).ShouldBe(
        [
            TrailStep.AutoApproved, TrailStep.RemedyRecommended, TrailStep.RemedyDecided, TrailStep.RepairCentreAssigned,
            TrailStep.ActionExecuted,
        ]);
        _entries.ShouldAllBe(e => e.Actor == "adjudication-service");

        var recommended = _entries.Single(e => e.Step == TrailStep.RemedyRecommended).Payload;
        recommended.GetProperty("policyVersionId").GetGuid().ShouldBe(VersionId);
        recommended.GetProperty("policyVersion").GetInt32().ShouldBe(2);
        recommended.GetProperty("tableRow").GetString().ShouldBe("coveredDefect");
        recommended.GetProperty("scenario").GetString().ShouldBe("CoveredDefect");
        recommended.GetProperty("allowedRemedies").EnumerateArray().Select(r => r.GetString()).ShouldBe(["Repair"]);

        var decided = _entries.Single(e => e.Step == TrailStep.RemedyDecided).Payload;
        decided.GetProperty("remedy").GetString().ShouldBe("Repair");
        decided.GetProperty("decidedBy").GetString().ShouldBe("Policy");
        decided.GetProperty("policyVersionId").GetGuid().ShouldBe(VersionId);
        decided.GetProperty("tableRow").GetString().ShouldBe("coveredDefect");

        var assigned = _entries.Single(e => e.Step == TrailStep.RepairCentreAssigned).Payload;
        assigned.GetProperty("serviceCenterId").GetGuid().ShouldBe(_suggested.Id);
        assigned.GetProperty("repairId").GetGuid().ShouldBe(AddedRepairs.Single().Id);

        _entries[^1].Payload.GetProperty("action").GetString().ShouldBe("notify_customer");
        await _notifications.Received(1).EnqueueAsync(claim.Id, ActionExecutor.ApprovedTemplate, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_reviewer_approval_sets_the_policy_remedy_in_the_same_way()
    {
        var claim = ArrangeUnderReview();

        await Executor().ExecuteReviewerDecisionAsync(ReviewerApproval(claim), Ct);

        claim.Status.ShouldBe(ClaimStatus.Approved);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.Reviewer);
        AddedRemedies.ShouldHaveSingleItem().Remedy.ShouldBe(RemedyType.Repair);
        AddedRepairs.ShouldHaveSingleItem().ActiveHandover!.ServiceCenterId.ShouldBe(_suggested.Id);
        _writes.ShouldAllBe(w => w.InTransaction);
        _entries.Select(e => e.Step).ShouldBe(
        [
            TrailStep.ReviewerDecided, TrailStep.RemedyRecommended, TrailStep.RemedyDecided, TrailStep.RepairCentreAssigned,
            TrailStep.ActionExecuted,
        ]);
        await _repairRequests.DidNotReceiveWithAnyArgs().CreateAsync(default, default, Ct);
    }

    [Fact]
    public async Task Refund_set_by_policy_calls_the_refund_port_exactly_once_with_the_claim_value_and_creates_no_repair()
    {
        UseRules(new RemedyRules(new RemedyChoice(RemedyType.Refund, null), null, null, null));
        var claim = ArrangeAutomaticApproval(out var action);

        await Executor().ExecuteAsync(action, Ct);

        AddedRemedies.ShouldHaveSingleItem().Remedy.ShouldBe(RemedyType.Refund);
        await _refunds.Received(1).RequestRefundAsync(claim.Id, ClaimValue, Arg.Any<CancellationToken>());
        _refunds.ReceivedCalls().Count().ShouldBe(1);
        AddedRepairs.ShouldBeEmpty();
        _entries.Select(e => e.Step).ShouldContain(TrailStep.RemedyDecided);
        _entries.ShouldContain(e => e.Step == TrailStep.ActionExecuted && e.Payload.GetProperty("action").GetString() == "request_refund");
    }

    // ── Remedy awaiting a reviewer ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_choice_leaves_the_claim_Approved_with_the_remedy_awaiting_a_decision_and_no_side_effects()
    {
        UseRules(new RemedyRules(new RemedyChoice(null, [RemedyType.Refund, RemedyType.Replacement]), null, null, null));
        var claim = ArrangeAutomaticApproval(out var action);

        await Executor().ExecuteAsync(action, Ct);

        claim.Status.ShouldBe(ClaimStatus.Approved);
        claim.FinalOutcome.ShouldBe(FinalOutcome.Approved);
        var remedy = AddedRemedies.ShouldHaveSingleItem();
        remedy.State.ShouldBe(RemedyState.AwaitingDecision);
        remedy.Remedy.ShouldBeNull();
        remedy.PendingReason.ShouldBe(RemedyPendingReason.PolicyChoice);
        remedy.AllowedRemedies.ShouldBe([RemedyType.Refund, RemedyType.Replacement], ignoreOrder: true);
        AddedDecisions.Select(d => d.Kind).ShouldBe([RemedyDecisionKind.Evaluated]);
        AddedRepairs.ShouldBeEmpty();
        await _refunds.DidNotReceiveWithAnyArgs().RequestRefundAsync(default, default, Ct);
        _entries.Select(e => e.Step).ShouldBe([TrailStep.AutoApproved, TrailStep.RemedyRecommended, TrailStep.ActionExecuted]);
        _entries[1].Payload.GetProperty("pendingReason").GetString().ShouldBe("POLICY_CHOICE");
    }

    [Fact]
    public async Task A_version_without_remedy_rules_sends_the_remedy_to_a_reviewer_with_NO_REMEDY_RULE()
    {
        UseRules(null);
        var claim = ArrangeAutomaticApproval(out var action);

        await Executor().ExecuteAsync(action, Ct);

        claim.Status.ShouldBe(ClaimStatus.Approved);
        var remedy = AddedRemedies.ShouldHaveSingleItem();
        remedy.State.ShouldBe(RemedyState.AwaitingDecision);
        remedy.PendingReason.ShouldBe(RemedyPendingReason.NoRemedyRule);
        remedy.AllowedRemedies.ShouldBeEmpty();
        AddedRepairs.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_retried_approval_does_not_set_a_second_remedy()
    {
        var claim = ArrangeAutomaticApproval(out var action);
        await Executor().ExecuteAsync(action, Ct);
        _writes.Clear();

        (await Executor().ExecuteAsync(action, Ct)).ShouldBe(ActionExecution.AlreadyApplied);

        claim.Status.ShouldBe(ClaimStatus.Approved);
        _writes.ShouldBeEmpty();
    }

    // ── Arrangement ──────────────────────────────────────────────────────────────────────────

    /// <summary>The applicable version (AUR-WP v2-like: NA and EU, open-ended) carries <paramref name="rules"/>.</summary>
    private void UseRules(RemedyRules? rules)
    {
        var terms = new CoverageTerms(
            new Dictionary<Region, int> { [Region.NA] = 12, [Region.EU] = 24 }, new Dictionary<string, int>(),
            AccidentalDamageTerms.NotCovered, [ExclusionCode.AccidentalDamage, ExclusionCode.LiquidDamage]);
        var version = PolicyVersion.Create(
            VersionId, Tenant, PolicyId, 2, new DateOnly(2025, 1, 1), null, [Region.NA, Region.EU], [], terms,
            "tenant-aurora/policies/AUR-WP-v2.md", "checksum", rules);
        _policies.GetVersionsAsync(Arg.Any<CancellationToken>()).Returns([version]);
        _policies.GetVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(version);
    }

    private static Claim NewClaim(Guid? id = null) => Claim.Submit(
        id ?? Guid.CreateVersion7(), Tenant, ClaimReference.Generate(), ClaimChannel.ClaimantPortal, Claim.ClaimantSubmitter, CustomerId,
        "claimant@synthetic-mail.test", null, "AUR-TAB10", ProductId, "SN-1001", new DateOnly(2026, 1, 15), "Aurora Store",
        ClaimValue, Region.NA, "The tablet does not power on after charging overnight.", SubmittedAt);

    /// <summary>A claim under evaluation whose run the real guardrail engine approves automatically.</summary>
    private Claim ArrangeAutomaticApproval(out ApprovedAction action)
    {
        var scenario = GuardrailScenario.ClearApprove();
        var claim = NewClaim(scenario.ClaimId);
        claim.StartEvaluation(SubmittedAt);

        var input = scenario.Build();
        var outcome = new GuardrailEngine().Evaluate(input);
        var run = AdjudicationRun.Start(scenario.RunId, Tenant, claim.Id, claim.CurrentRound, "corr-t220", SubmittedAt);
        var evaluation = GuardrailEvaluation.Create(scenario.RunId, Tenant, outcome.Checks, outcome.Disposition, outcome.Reasons, null, Now);
        var record = new RunRecord(run, input.Intake, [], input.Policy?.Clauses ?? [], null, input.Risk, input.Recommendation, evaluation);

        _claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);
        _adjudication.GetRunRecordAsync(scenario.RunId, Arg.Any<CancellationToken>()).Returns(record);
        action = outcome.Action.ShouldNotBeNull();
        action.Kind.ShouldBe(ActionKind.FinalizeApproved);
        action.ClaimId.ShouldBe(claim.Id);
        return claim;
    }

    /// <summary>A claim escalated to review whose latest run is <see cref="RunId"/>.</summary>
    private Claim ArrangeUnderReview()
    {
        var claim = NewClaim();
        claim.StartEvaluation(SubmittedAt);
        claim.EscalateToReview(SubmittedAt);
        var run = AdjudicationRun.Start(RunId, Tenant, claim.Id, claim.CurrentRound, "corr-t220", SubmittedAt);
        _claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);
        _adjudication.GetLatestRunAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(run);
        return claim;
    }

    private static ReviewDecision ReviewerApproval(Claim claim) => ReviewDecision.Create(
        Guid.CreateVersion7(), Tenant, claim.Id, RunId, "reviewer-sub-t220", "Aurora Reviewer", ReviewDecisionKind.Approve,
        justification: null, claimantExplanation: "Your tablet is covered and will be repaired.", requestedItems: null,
        latestRecommendation: null, Now);

    /// <summary>The executor with the remedy ports (T225).</summary>
    private ActionExecutor Executor() => new(
        new FakeTenantContext(Tenant), _claims, _adjudication, _reviews, _catalog, _policies, _network, _remedies, _repairs, _refunds,
        _notifications, _trail, _unitOfWork, new RemedyPolicyEvaluator(), _time);

    private sealed record TrailRecord(TrailStep Step, string Actor, JsonElement Payload);
}
