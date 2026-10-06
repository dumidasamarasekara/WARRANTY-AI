using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Warranty.Application;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Actions;
using Warranty.Application.Claims;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Guardrails;
using Warranty.UnitTests.Guardrails;
using Warranty.UnitTests.Infrastructure;

namespace Warranty.UnitTests.Application;

/// <summary>
/// The ActionExecutor (T067): each guardrail-issued action moves the claim, writes its trail entries and
/// calls the simulated integrations in order; foreign-tenant, stale and inconsistent actions are refused;
/// a retried action changes nothing twice. Actions come from the real <see cref="GuardrailEngine"/>.
/// </summary>
public sealed class ActionExecutorTests
{
    private static readonly Guid Tenant = GuardrailScenario.Tenant;
    private static readonly Guid CustomerId = Guid.Parse("0199b000-0000-7000-8000-0000000000d7");
    private static readonly Guid ProductId = Guid.Parse("0199b000-0000-7000-8000-0000000000e7");
    private static readonly DateTimeOffset SubmittedAt = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 5, 0, TimeSpan.Zero);

    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly IAdjudicationRepository _adjudication = Substitute.For<IAdjudicationRepository>();
    private readonly ICatalogRepository _catalog = Substitute.For<ICatalogRepository>();
    private readonly IServiceNetwork _network = Substitute.For<IServiceNetwork>();
    private readonly IRepairRequestService _repairs = Substitute.For<IRepairRequestService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IDecisionTrailWriter _trail = Substitute.For<IDecisionTrailWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly List<TrailRecord> _entries = [];
    private readonly ServiceCenterInfo _center = new(Guid.Parse("0199b000-0000-7000-8000-0000000000f1"), "Aurora Service North", Region.NA);
    private readonly Guid _repairRequestId = Guid.Parse("0199b000-0000-7000-8000-0000000000a1");
    private readonly Guid _notificationId = Guid.Parse("0199b000-0000-7000-8000-0000000000a2");

    public ActionExecutorTests()
    {
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

    // ── Each action kind ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FinalizeApproved_approves_as_the_system_with_the_run_explanation_then_creates_a_repair_request_and_notifies()
    {
        var scenario = GuardrailScenario.ClearApprove();
        var (claim, action) = Arrange(scenario);
        action.Kind.ShouldBe(ActionKind.FinalizeApproved);

        var result = await Executor().ExecuteAsync(action, Ct);

        result.ShouldBe(ActionExecution.Executed);
        claim.Status.ShouldBe(ClaimStatus.Approved);
        claim.FinalOutcome.ShouldBe(FinalOutcome.Approved);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.System);
        claim.FinalExplanation.ShouldBe(GuardrailScenario.SafeApproveText);
        claim.FinalizedAt.ShouldBe(Now);

        Received.InOrder(() =>
        {
            _network.FindServiceCenterAsync(Region.NA, "tablet", Arg.Any<CancellationToken>());
            _repairs.CreateAsync(claim.Id, _center.Id, Arg.Any<CancellationToken>());
            _notifications.EnqueueAsync(claim.Id, ActionExecutor.ApprovedTemplate, Arg.Any<CancellationToken>());
        });

        _entries.Select(e => e.Step).ShouldBe([TrailStep.AutoApproved, TrailStep.ActionExecuted, TrailStep.ActionExecuted]);
        _entries.ShouldAllBe(e => e.ClaimId == claim.Id && e.Actor == "adjudication-service");
        _entries[0].Payload.GetProperty("runId").GetGuid().ShouldBe(scenario.RunId);
        _entries[1].Payload.GetProperty("action").GetString().ShouldBe("create_repair_request");
        _entries[1].Payload.GetProperty("repairRequestId").GetGuid().ShouldBe(_repairRequestId);
        _entries[2].Payload.GetProperty("action").GetString().ShouldBe("notify_customer");
        _entries[2].Payload.GetProperty("notificationId").GetGuid().ShouldBe(_notificationId);
        await _unitOfWork.ReceivedWithAnyArgs(1).ExecuteInTransactionAsync(default!, Ct);
    }

    [Fact]
    public async Task FinalizeRejected_rejects_as_the_system_and_notifies_without_a_repair_request()
    {
        var (claim, action) = Arrange(GuardrailScenario.ClearReject());
        action.Kind.ShouldBe(ActionKind.FinalizeRejected);

        (await Executor().ExecuteAsync(action, Ct)).ShouldBe(ActionExecution.Executed);

        claim.Status.ShouldBe(ClaimStatus.Rejected);
        claim.FinalOutcome.ShouldBe(FinalOutcome.Rejected);
        claim.FinalDecidedBy.ShouldBe(DecidedBy.System);
        claim.FinalExplanation.ShouldBe(GuardrailScenario.SafeRejectText);
        claim.FinalizedAt.ShouldBe(Now);
        await _notifications.Received(1).EnqueueAsync(claim.Id, ActionExecutor.RejectedTemplate, Arg.Any<CancellationToken>());
        await _network.DidNotReceiveWithAnyArgs().FindServiceCenterAsync(default, default!, Ct);
        await _repairs.DidNotReceiveWithAnyArgs().CreateAsync(default, default, Ct);
        _entries.Select(e => e.Step).ShouldBe([TrailStep.AutoRejected, TrailStep.ActionExecuted]);
        _entries[1].Payload.GetProperty("action").GetString().ShouldBe("notify_customer");
    }

    [Fact]
    public async Task EscalateToReview_moves_the_claim_to_review_and_records_the_guardrail_reasons()
    {
        var scenario = GuardrailScenario.ClearApprove();
        scenario.Decision = AiDecision.HumanReview;
        var (claim, action) = Arrange(scenario);
        action.Kind.ShouldBe(ActionKind.EscalateToReview);

        (await Executor().ExecuteAsync(action, Ct)).ShouldBe(ActionExecution.Executed);

        claim.Status.ShouldBe(ClaimStatus.UnderReview);
        claim.FinalOutcome.ShouldBeNull();
        claim.FinalExplanation.ShouldBeNull();
        _entries.Select(e => e.Step).ShouldBe([TrailStep.EscalatedToReview]);
        _entries[0].Payload.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ShouldBe(["AI_RECOMMENDS_REVIEW"]);
        ShouldNotHaveCalledIntegrations();
    }

    [Fact]
    public async Task RequestInformation_pauses_the_claim_with_the_items_and_counts_the_automatic_request()
    {
        var scenario = GuardrailScenario.ClearApprove();
        scenario.IntakeMissingItems.Add(RequestedItem.Create("LEGIBLE_INVOICE", "The invoice could not be read."));
        var (claim, action) = Arrange(scenario);
        action.Kind.ShouldBe(ActionKind.RequestInformation);

        (await Executor().ExecuteAsync(action, Ct)).ShouldBe(ActionExecution.Executed);

        claim.Status.ShouldBe(ClaimStatus.PendingInformation);
        claim.RequestedItems.ShouldBe([RequestedItem.Create("LEGIBLE_INVOICE", "The invoice could not be read.")]);
        claim.AutoInfoRequestCount.ShouldBe(1);
        claim.ReviewerInfoRequested.ShouldBeFalse();
        _entries.Select(e => e.Step).ShouldBe([TrailStep.InformationRequested]);
        _entries[0].Payload.GetProperty("items").EnumerateArray().Select(i => i.GetString()).ShouldBe(["LEGIBLE_INVOICE"]);
        _entries[0].Payload.GetProperty("autoInfoRequestCount").GetInt32().ShouldBe(1);
        ShouldNotHaveCalledIntegrations();
    }

    [Fact]
    public async Task RequestInformation_stores_claimant_facing_text_in_place_of_an_unsafe_or_blank_reason()
    {
        // Reasons can come from the model's missingInformation; the claim keeps only text safe to show the claimant.
        var scenario = GuardrailScenario.ClearApprove();
        scenario.IntakeMissingItems.Add(new RequestedItem("INVOICE", "Upload the invoice; EV-1 looks suspicious."));
        scenario.IntakeMissingItems.Add(new RequestedItem("PHOTO_OF_DAMAGE", " "));
        var (claim, action) = Arrange(scenario);
        action.Kind.ShouldBe(ActionKind.RequestInformation);

        await Executor().ExecuteAsync(action, Ct);

        claim.RequestedItems.ShouldBe(
        [
            new RequestedItem("INVOICE", RequestedItemCatalog.ClaimantText("INVOICE")),
            new RequestedItem("PHOTO_OF_DAMAGE", RequestedItemCatalog.ClaimantText("PHOTO_OF_DAMAGE")),
        ]);
    }

    [Fact]
    public async Task A_third_automatic_information_request_is_refused_by_the_domain()
    {
        // The guardrails escalate at a count of 2; facts read before a concurrent change could still issue a
        // request, and the claim itself is the last line of defense.
        var scenario = GuardrailScenario.ClearApprove();
        scenario.AutoInfoRequestCount = 1;
        scenario.IntakeMissingItems.Add(RequestedItem.Create("PHOTO_OF_SERIAL", "The serial label is not visible."));
        var (claim, action) = Arrange(scenario, prepare: c =>
        {
            RequestAndSupplement(c);
            RequestAndSupplement(c);
        });
        action.Kind.ShouldBe(ActionKind.RequestInformation);
        claim.AutoInfoRequestCount.ShouldBe(2);

        await Should.ThrowAsync<InvalidClaimTransitionException>(() => Executor().ExecuteAsync(action, Ct));

        claim.Status.ShouldBe(ClaimStatus.UnderEvaluation);
        claim.AutoInfoRequestCount.ShouldBe(2);
        _entries.ShouldBeEmpty();
        ShouldNotHaveCalledIntegrations();
    }

    [Fact]
    public async Task Without_a_service_center_the_approval_stands_and_the_trail_records_that_no_repair_request_was_created()
    {
        _network.FindServiceCenterAsync(Region.NA, "tablet", Arg.Any<CancellationToken>()).Returns((ServiceCenterInfo?)null);
        var (claim, action) = Arrange(GuardrailScenario.ClearApprove());

        await Executor().ExecuteAsync(action, Ct);

        claim.Status.ShouldBe(ClaimStatus.Approved);
        await _repairs.DidNotReceiveWithAnyArgs().CreateAsync(default, default, Ct);
        await _notifications.Received(1).EnqueueAsync(claim.Id, ActionExecutor.ApprovedTemplate, Arg.Any<CancellationToken>());
        _entries.Select(e => e.Payload.TryGetProperty("action", out var a) ? a.GetString() : null)
            .ShouldBe([null, "service_network_lookup", "notify_customer"]);
    }

    // ── Refusals (fail closed) ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_action_for_another_tenant_than_the_current_scope_is_refused_before_anything_is_read()
    {
        var (claim, action) = Arrange(GuardrailScenario.ClearApprove());
        var executor = Executor(new FakeTenantContext(Guid.Parse("0199b000-0000-7000-8000-00000000beef")));

        await Should.ThrowAsync<ActionRefusedException>(() => executor.ExecuteAsync(action, Ct));

        claim.Status.ShouldBe(ClaimStatus.UnderEvaluation);
        await _claims.DidNotReceiveWithAnyArgs().GetAsync(default, Ct);
        await _unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!, Ct);
        _entries.ShouldBeEmpty();
        ShouldNotHaveCalledIntegrations();
    }

    [Fact]
    public async Task An_action_without_a_resolved_tenant_is_refused()
    {
        var (_, action) = Arrange(GuardrailScenario.ClearApprove());

        await Should.ThrowAsync<ActionRefusedException>(() => Executor(new FakeTenantContext(null)).ExecuteAsync(action, Ct));

        _entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_action_from_an_earlier_round_is_refused()
    {
        var scenario = GuardrailScenario.ClearApprove();
        var (claim, action) = Arrange(scenario, prepare: RequestAndSupplement, runRound: 1);
        claim.CurrentRound.ShouldBe(2);

        await Should.ThrowAsync<ActionRefusedException>(() => Executor().ExecuteAsync(action, Ct));

        claim.Status.ShouldBe(ClaimStatus.UnderEvaluation);
        _entries.ShouldBeEmpty();
        ShouldNotHaveCalledIntegrations();
    }

    [Fact]
    public async Task A_finalization_whose_run_recommendation_disagrees_is_refused()
    {
        var (claim, action) = Arrange(GuardrailScenario.ClearApprove());
        var rejectInput = GuardrailScenario.ClearReject().Build();
        var record = await _adjudication.GetRunRecordAsync(action.RunId, Ct);
        _adjudication.GetRunRecordAsync(action.RunId, Arg.Any<CancellationToken>())
            .Returns(record! with { Recommendation = rejectInput.Recommendation, Guardrails = null });

        await Should.ThrowAsync<ActionRefusedException>(() => Executor().ExecuteAsync(action, Ct));

        claim.Status.ShouldBe(ClaimStatus.UnderEvaluation);
        _entries.ShouldBeEmpty();
        ShouldNotHaveCalledIntegrations();
    }

    [Fact]
    public async Task An_action_that_disagrees_with_the_recorded_guardrail_disposition_is_refused()
    {
        var (claim, action) = Arrange(GuardrailScenario.ClearApprove());
        var escalation = GuardrailScenario.ClearApprove();
        escalation.Decision = AiDecision.HumanReview;
        var escalated = escalation.Evaluate();
        var record = await _adjudication.GetRunRecordAsync(action.RunId, Ct);
        _adjudication.GetRunRecordAsync(action.RunId, Arg.Any<CancellationToken>()).Returns(record! with
        {
            Guardrails = GuardrailEvaluation.Create(
                action.RunId, Tenant, escalated.Checks, escalated.Disposition, escalated.Reasons, null, Now),
        });

        await Should.ThrowAsync<ActionRefusedException>(() => Executor().ExecuteAsync(action, Ct));

        claim.Status.ShouldBe(ClaimStatus.UnderEvaluation);
        _entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_claim_that_already_reached_another_state_is_not_finalized()
    {
        var (claim, action) = Arrange(GuardrailScenario.ClearApprove(), prepare: c => c.EscalateToReview(SubmittedAt));

        await Should.ThrowAsync<InvalidClaimTransitionException>(() => Executor().ExecuteAsync(action, Ct));

        claim.Status.ShouldBe(ClaimStatus.UnderReview);
        claim.FinalOutcome.ShouldBeNull();
        _entries.ShouldBeEmpty();
        ShouldNotHaveCalledIntegrations();
    }

    // ── Retry safety ───────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> Kinds => new("approve", "reject", "escalate", "request-information");

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Executing_the_same_action_again_changes_and_writes_nothing(string kind)
    {
        var (claim, action) = Arrange(ScenarioFor(kind));
        var executor = Executor();
        (await executor.ExecuteAsync(action, Ct)).ShouldBe(ActionExecution.Executed);
        var status = claim.Status;
        var updatedAt = claim.UpdatedAt;
        var count = claim.AutoInfoRequestCount;
        var entries = _entries.Count;
        var integrationCalls = IntegrationCallCount();
        _time.Advance(TimeSpan.FromMinutes(1));

        (await executor.ExecuteAsync(action, Ct)).ShouldBe(ActionExecution.AlreadyApplied);

        claim.Status.ShouldBe(status);
        claim.UpdatedAt.ShouldBe(updatedAt);
        claim.AutoInfoRequestCount.ShouldBe(count);
        _entries.Count.ShouldBe(entries);
        IntegrationCallCount().ShouldBe(integrationCalls);
    }

    [Fact]
    public async Task A_retried_approval_creates_exactly_one_repair_request_and_one_notification()
    {
        var (_, action) = Arrange(GuardrailScenario.ClearApprove());
        var executor = Executor();

        await executor.ExecuteAsync(action, Ct);
        await executor.ExecuteAsync(action, Ct);

        await _repairs.ReceivedWithAnyArgs(1).CreateAsync(default, default, Ct);
        await _notifications.ReceivedWithAnyArgs(1).EnqueueAsync(default, default!, Ct);
    }

    [Fact]
    public void The_application_registers_the_executor_per_scope()
    {
        var services = new ServiceCollection();

        services.AddWarrantyApplication();

        services.Single(d => d.ServiceType == typeof(IActionExecutor)).Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static GuardrailScenario ScenarioFor(string kind)
    {
        switch (kind)
        {
            case "approve":
                return GuardrailScenario.ClearApprove();
            case "reject":
                return GuardrailScenario.ClearReject();
            case "escalate":
                var escalate = GuardrailScenario.ClearApprove();
                escalate.Decision = AiDecision.HumanReview;
                return escalate;
            default:
                var request = GuardrailScenario.ClearApprove();
                request.IntakeMissingItems.Add(RequestedItem.Create("LEGIBLE_INVOICE", "The invoice could not be read."));
                return request;
        }
    }

    /// <summary>
    /// A claim under evaluation behind the repository, its run (in the claim's round unless
    /// <paramref name="runRound"/> says otherwise) with the scenario's recommendation and recorded
    /// guardrail evaluation, and the action the real engine issued for the scenario.
    /// </summary>
    private (Claim Claim, ApprovedAction Action) Arrange(GuardrailScenario scenario, Action<Claim>? prepare = null, int? runRound = null)
    {
        var claim = Claim.Submit(
            scenario.ClaimId, Tenant, ClaimReference.Generate(), ClaimChannel.ClaimantPortal, Claim.ClaimantSubmitter, CustomerId,
            "claimant@synthetic-mail.test", null, "AUR-TAB10", ProductId, "SN-1001", new DateOnly(2026, 1, 15), "Brightline Electronics",
            349m, Region.NA, "The tablet does not power on after charging overnight.", SubmittedAt);
        claim.StartEvaluation(SubmittedAt);
        prepare?.Invoke(claim);

        var input = scenario.Build();
        var outcome = new GuardrailEngine().Evaluate(input);
        var run = AdjudicationRun.Start(scenario.RunId, Tenant, claim.Id, runRound ?? claim.CurrentRound, "corr-t067", SubmittedAt);
        var evaluation = GuardrailEvaluation.Create(scenario.RunId, Tenant, outcome.Checks, outcome.Disposition, outcome.Reasons, null, Now);
        var record = new RunRecord(run, input.Intake, [], input.Policy?.Clauses ?? [], null, input.Risk, input.Recommendation, evaluation);

        _claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);
        _adjudication.GetRunRecordAsync(scenario.RunId, Arg.Any<CancellationToken>()).Returns(record);
        return (claim, outcome.Action.ShouldNotBeNull());
    }

    private static void RequestAndSupplement(Claim claim)
    {
        claim.RequestInformation([RequestedItem.Create("LEGIBLE_INVOICE", "The invoice could not be read.")], DecidedBy.System, SubmittedAt);
        claim.AddSupplement(SubmittedAt);
    }

    private ActionExecutor Executor(FakeTenantContext? tenant = null) => new(
        tenant ?? new FakeTenantContext(Tenant), _claims, _adjudication, Substitute.For<IReviewRepository>(), _catalog, _network, _repairs, _notifications, _trail, _unitOfWork,
        _time);

    private int IntegrationCallCount()
        => _network.ReceivedCalls().Count() + _repairs.ReceivedCalls().Count() + _notifications.ReceivedCalls().Count();

    private void ShouldNotHaveCalledIntegrations() => IntegrationCallCount().ShouldBe(0);

    private sealed record TrailRecord(Guid ClaimId, TrailStep Step, string Actor, string Summary, JsonElement Payload);
}
