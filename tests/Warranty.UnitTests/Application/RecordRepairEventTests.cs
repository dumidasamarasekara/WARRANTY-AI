using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Warranty.Application;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Fulfillment;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Remedies;
using Warranty.UnitTests.Domain;

namespace Warranty.UnitTests.Application;

/// <summary>
/// Staff repair updates (002 T235; FR-019 – FR-028, FR-038, research R5 – R7): <see cref="RecordRepairEvent"/> refuses
/// a <c>reportedAt</c> in the future or before the hand-over and missing event fields (400 naming the field), a repair
/// centre that is not an eligible centre of the tenant (404 + <c>ACCESS_DENIED</c>), a missing or stale repair ETag
/// (412) and an event the current status does not allow (409 naming the status and the allowed events) — all without
/// changing the repair. <c>MarkedReady</c> queues exactly one <c>RepairReadyForCollection</c> notification with the
/// dedupe key <c>repair-ready:{attemptId}</c>; <c>Collected</c> completes the remedy. A failure or an uneconomical estimate
/// re-evaluates the remedy with the claim's own rules (T248); transfers and corrections follow FR-026 and FR-027 (T256).
/// Implemented in T239.
/// </summary>
public sealed class RecordRepairEventTests
{
    private const string AgentSub = "agent-sub-t235";
    private const string AgentName = "Aurora Agent";
    private const uint RowVersion = 7341;
    private const uint RemedyRowVersion = 5150;
    private const string ValidIfMatch = "\"7341\"";
    private const string Reason = "The customer asked for a centre closer to home.";
    private const decimal ClaimValue = 349m;

    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OtherTenant = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid CustomerId = Guid.Parse("0199b000-0000-7000-8000-0000000004d7");
    private static readonly Guid ProductId = Guid.Parse("0199b000-0000-7000-8000-0000000004e7");
    private static readonly Guid PolicyId = Guid.Parse("0199b000-0000-7000-8000-0000000004a1");
    private static readonly Guid VersionId = Guid.Parse("0199b000-0000-7000-8000-0000000004b2");
    private static readonly Guid SuggestedCentre = Guid.Parse("33333333-3333-7333-8333-333333333331");
    private static readonly Guid OtherCentre = Guid.Parse("33333333-3333-7333-8333-333333333332");

    /// <summary>An active centre of the other tenant: never in this tenant's eligible list.</summary>
    private static readonly Guid ForeignCentre = Guid.Parse("44444444-4444-7444-8444-444444444441");

    private static readonly DateTimeOffset SubmittedAt = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ApprovedAt = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly HandedOverOn = new(2026, 9, 4);
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 14, 30, 0, TimeSpan.Zero);

    private readonly ITenantContext _tenant = Substitute.For<ITenantContext>();
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly ICatalogRepository _catalog = Substitute.For<ICatalogRepository>();
    private readonly IRemedyRepository _remedies = Substitute.For<IRemedyRepository>();
    private readonly IRepairRepository _repairs = Substitute.For<IRepairRepository>();
    private readonly IPolicyRepository _policies = Substitute.For<IPolicyRepository>();
    private readonly List<RemedyDecision> _decisions = [];
    private readonly IServiceNetwork _network = Substitute.For<IServiceNetwork>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IDecisionTrailWriter _trail = Substitute.For<IDecisionTrailWriter>();
    private readonly ISecurityEventWriter _securityEvents = Substitute.For<ISecurityEventWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly List<TrailRecord> _entries = [];
    private ClaimRemedy? _remedy;

    public RecordRepairEventTests()
    {
        _tenant.IsResolved.Returns(true);
        _tenant.TenantId.Returns(Tenant);
        _tenant.PrincipalId.Returns(AgentSub);
        _tenant.PrincipalName.Returns(AgentName);
        _tenant.IsSystem.Returns(false);

        _unitOfWork.ExecuteInTransactionAsync(default!, default)
            .ReturnsForAnyArgs(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        _trail.WhenForAnyArgs(t => t.AppendAsync(default, default, default!, default!, default, default))
            .Do(call => _entries.Add(new TrailRecord(
                call.ArgAt<TrailStep>(1), call.ArgAt<string>(2), JsonSerializer.SerializeToElement(call.ArgAt<object?>(4)))));
        _catalog.GetProductAsync(ProductId, Arg.Any<CancellationToken>())
            .Returns(Product.Create(ProductId, Tenant, "AUR-TAB10", "Aurora Tab 10", "tablet", ClaimValue, "USD"));
        _network.ListEligibleAsync(Region.NA, "tablet", Arg.Any<CancellationToken>()).Returns(
        [
            new EligibleServiceCenter(SuggestedCentre, "Aurora Service Center Springfield", Region.NA, "1 Main St", "Mon-Fri 9-17", "555-0100", 10, true),
            new EligibleServiceCenter(OtherCentre, "Aurora Service Center Shelbyville", Region.NA, "2 Oak Ave", "Mon-Sat 9-18", "555-0101", 20, true),
        ]);
        _notifications.EnqueueAsync(default, default!, default!, default)
            .ReturnsForAnyArgs(NotificationEnqueueResult.Queued(Guid.Parse("0199b000-0000-7000-8000-0000000004f1")));
        _remedies.GetDecisionsAsync(default, default).ReturnsForAnyArgs(_ => _decisions.ToList());
        _remedies.WhenForAnyArgs(r => r.AddDecision(default!)).Do(call => _decisions.Add(call.Arg<RemedyDecision>()));
        UseRules(RemedyRulesContract.Aurora, "AUR-WP");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Reported time (FR-025) ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reportedAt_in_the_future_is_refused()
    {
        var (claim, repair) = ArrangeAtRepairCentre();

        var result = await Execute(claim, MarkedReady() with { ReportedAt = Now.AddMinutes(5) });

        ShouldBeInvalid(result, "reportedAt");
        ShouldBeUnchanged(repair, RepairStatus.AtRepairCentre);
    }

    [Fact]
    public async Task A_reportedAt_before_the_hand_over_is_refused()
    {
        var (claim, repair) = ArrangeAtRepairCentre();
        var dayBefore = new DateTimeOffset(HandedOverOn.AddDays(-1).ToDateTime(new TimeOnly(16, 0)), TimeSpan.Zero);

        var result = await Execute(claim, MarkedReady() with { ReportedAt = dayBefore });

        ShouldBeInvalid(result, "reportedAt");
        ShouldBeUnchanged(repair, RepairStatus.AtRepairCentre);
    }

    [Fact]
    public async Task A_reportedAt_on_the_hand_over_day_or_now_is_accepted()
    {
        var (claim, _) = ArrangeAtRepairCentre();
        var handOverDay = new DateTimeOffset(HandedOverOn.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero);

        (await Execute(claim, MarkedReady() with { ReportedAt = handOverDay })).ShouldBeOfType<RecordRepairEventResult.Recorded>();

        var (other, _) = ArrangeAtRepairCentre();
        (await Execute(other, MarkedReady() with { ReportedAt = Now })).ShouldBeOfType<RecordRepairEventResult.Recorded>();
    }

    // ── Event fields (contracts RepairEventRequest) ────────────────────────────────────────────

    public static TheoryData<string, string> MissingOrInvalidFields => new()
    {
        { "noType", "type" },
        { "noReportedAt", "reportedAt" },
        { "noSource", "source" },
        { "systemSource", "source" },
        { "noteTooLong", "note" },
        { "centreChangeWithoutCentre", "repairCentreId" },
        { "centreChangeWithoutReason", "reason" },
        { "centreChangeWithShortReason", "reason" },
        { "centreChangeWithLongReason", "reason" },
        { "handOverWithoutDate", "handedOverOn" },
        { "handOverInTheFuture", "handedOverOn" },
        { "handOverWithLongReference", "centreReference" },
        { "estimateWithoutAmount", "estimateAmount" },
        { "negativeEstimate", "estimateAmount" },
        { "estimateWithThreeDecimals", "estimateAmount" },
        { "failureWithoutReason", "reason" },
        { "failureWithShortReason", "reason" },
        { "newAttemptWithoutLocation", "productLocation" },
    };

    [Theory]
    [MemberData(nameof(MissingOrInvalidFields))]
    public async Task Missing_or_invalid_event_fields_are_refused_naming_the_field(string request, string field)
    {
        var (claim, repair) = ArrangeAwaitingHandover();
        var command = request switch
        {
            "noType" => HandedOver() with { Type = null },
            "noReportedAt" => HandedOver() with { ReportedAt = null },
            "noSource" => HandedOver() with { Source = null },
            "systemSource" => HandedOver() with { Source = ReportSource.System },
            "noteTooLong" => HandedOver() with { Note = new string('n', RepairHandover.MaxNoteLength + 1) },
            "centreChangeWithoutCentre" => CentreChanged() with { RepairCentreId = null },
            "centreChangeWithoutReason" => CentreChanged() with { Reason = null },
            "centreChangeWithShortReason" => CentreChanged() with { Reason = "too short" },
            "centreChangeWithLongReason" => CentreChanged() with { Reason = new string('r', 1001) },
            "handOverWithoutDate" => HandedOver() with { HandedOverOn = null },
            "handOverInTheFuture" => HandedOver() with { HandedOverOn = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(1) },
            "handOverWithLongReference" => HandedOver() with { CentreReference = new string('J', RepairHandover.MaxCentreReferenceLength + 1) },
            "estimateWithoutAmount" => Estimate(0m) with { EstimateAmount = null },
            "negativeEstimate" => Estimate(-0.01m),
            "estimateWithThreeDecimals" => Estimate(100.005m),
            "failureWithoutReason" => Failed() with { Reason = null },
            "failureWithShortReason" => Failed() with { Reason = "broken" },
            "newAttemptWithoutLocation" => NewAttempt() with { ProductLocation = null },
            _ => throw new ArgumentOutOfRangeException(nameof(request), request, null),
        };

        var result = await Execute(claim, command);

        ShouldBeInvalid(result, field);
        ShouldBeUnchanged(repair, RepairStatus.AwaitingHandover);
    }

    // ── Tenant isolation (FR-022) ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_centre_of_another_tenant_is_not_found_and_records_ACCESS_DENIED()
    {
        var (claim, repair) = ArrangeAwaitingHandover();

        var result = await Execute(claim, CentreChanged() with { RepairCentreId = ForeignCentre });

        result.ShouldBeOfType<RecordRepairEventResult.NotFound>();
        await _securityEvents.Received(1).RecordAsync(
            SecurityEventKind.AccessDenied, AgentName, Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
        ShouldBeUnchanged(repair, RepairStatus.AwaitingHandover);
        repair.ActiveHandover.ShouldNotBeNull().ServiceCenterId.ShouldBe(SuggestedCentre);
    }

    [Fact]
    public async Task An_unknown_claim_or_a_claim_of_another_tenant_is_not_found()
    {
        (await UseCase().ExecuteAsync(MarkedReady() with { ClaimId = Guid.NewGuid() }, Ct))
            .ShouldBeOfType<RecordRepairEventResult.NotFound>();

        var foreign = NewClaim(OtherTenant);
        _claims.GetAsync(foreign.Id, Arg.Any<CancellationToken>()).Returns(foreign);
        (await Execute(foreign, MarkedReady())).ShouldBeOfType<RecordRepairEventResult.NotFound>();

        await _unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!, Ct);
        _notifications.ReceivedCalls().ShouldBeEmpty();
    }

    // ── Concurrency (FR-028, research R5) ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("W/\"7341\"")]
    [InlineData("\"7340\"")]
    [InlineData("not-a-version")]
    public async Task A_missing_or_stale_If_Match_fails_the_precondition(string? ifMatch)
    {
        var (claim, repair) = ArrangeAtRepairCentre();

        var result = await Execute(claim, MarkedReady() with { IfMatch = ifMatch });

        result.ShouldBeOfType<RecordRepairEventResult.PreconditionFailed>();
        ShouldBeUnchanged(repair, RepairStatus.AtRepairCentre);
    }

    [Fact]
    public async Task The_remedy_ETag_is_not_the_repair_ETag()
    {
        var (claim, repair) = ArrangeAtRepairCentre();

        var result = await Execute(claim, MarkedReady() with { IfMatch = $"\"{RemedyRowVersion}\"" });

        result.ShouldBeOfType<RecordRepairEventResult.PreconditionFailed>();
        ShouldBeUnchanged(repair, RepairStatus.AtRepairCentre);
    }

    // ── Refused transitions (FR-024) ───────────────────────────────────────────────────────────

    [Fact]
    public async Task An_event_the_status_does_not_allow_conflicts_naming_the_status_and_the_allowed_events()
    {
        var (claim, repair) = ArrangeAwaitingHandover();

        var result = await Execute(claim, MarkedReady());

        var conflict = result.ShouldBeOfType<RecordRepairEventResult.Conflict>();
        conflict.Status.ShouldBe(RepairStatus.AwaitingHandover);
        conflict.AllowedEvents.ShouldBe(repair.AllowedEvents(), ignoreOrder: true);
        conflict.AllowedEvents.ShouldContain(RepairEventType.HandedOver);
        conflict.AllowedEvents.ShouldNotContain(RepairEventType.MarkedReady);
        conflict.Detail.ShouldContain(nameof(RepairStatus.AwaitingHandover));
        conflict.Detail.ShouldContain(nameof(RepairEventType.HandedOver));
        ShouldBeUnchanged(repair, RepairStatus.AwaitingHandover);
    }

    [Fact]
    public async Task A_claim_without_a_repair_conflicts()
    {
        var claim = NewClaim();
        ArrangeApproved(claim, ClaimRemedy.AwaitDecision(
            Guid.CreateVersion7(), Tenant, claim.Id, VersionId, RemedyScenario.CoveredDefect,
            [RemedyType.Refund, RemedyType.Replacement], RemedyPendingReason.PolicyChoice, ApprovedAt), repair: null);

        var result = await Execute(claim, HandedOver());

        result.ShouldBeOfType<RecordRepairEventResult.Conflict>();
        await _unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!, Ct);
    }

    // ── Recorded events ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_hand_over_moves_the_repair_to_the_centre_with_one_event_and_one_trail_entry_in_one_transaction()
    {
        var (claim, repair) = ArrangeAwaitingHandover();
        var events = CaptureEvents();

        var result = await Execute(claim, HandedOver());

        result.ShouldBeOfType<RecordRepairEventResult.Recorded>().Repair.ShouldBeSameAs(repair);
        repair.Status.ShouldBe(RepairStatus.AtRepairCentre);
        var recorded = events.ShouldHaveSingleItem();
        (recorded.Type, recorded.Actor, recorded.Source, recorded.RecordedAt).ShouldBe(
            (RepairEventType.HandedOver, AgentSub, ReportSource.Phone, Now));
        recorded.ReportedAt.ShouldBe(HandedOver().ReportedAt!.Value);
        var entry = _entries.ShouldHaveSingleItem();
        (entry.Step, entry.Actor).ShouldBe((TrailStep.RepairHandedOver, AgentSub));
        await _unitOfWork.ReceivedWithAnyArgs(1).ExecuteInTransactionAsync(default!, Ct);
        _notifications.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_centre_change_to_an_eligible_centre_of_the_tenant_is_recorded()
    {
        var (claim, repair) = ArrangeAwaitingHandover();

        var result = await Execute(claim, CentreChanged());

        result.ShouldBeOfType<RecordRepairEventResult.Recorded>();
        var active = repair.ActiveHandover.ShouldNotBeNull();
        (active.ServiceCenterId, active.Assignment, active.Reason).ShouldBe((OtherCentre, HandoverAssignment.ChosenByStaff, Reason));
        _entries.Select(e => e.Step).ShouldBe([TrailStep.RepairCentreAssigned]);
        await _securityEvents.DidNotReceiveWithAnyArgs().RecordAsync(default, default!, default, default, Ct);
    }

    [Fact]
    public async Task Marking_ready_queues_one_ready_notification_keyed_by_the_attempt()
    {
        var (claim, repair) = ArrangeAtRepairCentre();
        var attemptId = repair.CurrentAttempt.ShouldNotBeNull().Id;

        var result = await Execute(claim, MarkedReady());

        result.ShouldBeOfType<RecordRepairEventResult.Recorded>();
        repair.Status.ShouldBe(RepairStatus.ReadyForCollection);
        await _notifications.Received(1).EnqueueAsync(
            claim.Id, "RepairReadyForCollection", $"repair-ready:{attemptId}", Arg.Any<CancellationToken>());
        _notifications.ReceivedCalls().Count().ShouldBe(1, "only the keyed overload, once");
        _entries.Select(e => e.Step).ShouldContain(TrailStep.RepairStatusUpdated);
        await _unitOfWork.ReceivedWithAnyArgs(1).ExecuteInTransactionAsync(default!, Ct);
    }

    [Fact]
    public async Task A_ready_notification_already_queued_for_the_attempt_still_records_the_event()
    {
        var (claim, repair) = ArrangeAtRepairCentre();
        _notifications.EnqueueAsync(default, default!, default!, Ct).ReturnsForAnyArgs(NotificationEnqueueResult.Duplicate);

        var result = await Execute(claim, MarkedReady());

        result.ShouldBeOfType<RecordRepairEventResult.Recorded>();
        repair.Status.ShouldBe(RepairStatus.ReadyForCollection);
    }

    [Fact]
    public async Task Collection_completes_the_repair_and_the_remedy()
    {
        var (claim, repair) = ArrangeAtRepairCentre();
        repair.MarkReady(Report(1));
        var remedy = _remedy.ShouldNotBeNull();

        var result = await Execute(claim, Collected());

        result.ShouldBeOfType<RecordRepairEventResult.Recorded>();
        repair.Status.ShouldBe(RepairStatus.Collected);
        remedy.State.ShouldBe(RemedyState.Completed);
        remedy.Remedy.ShouldBe(RemedyType.Repair);
        _entries.Select(e => e.Step).ShouldContain(TrailStep.RemedyCompleted);
        _notifications.ReceivedCalls().ShouldBeEmpty();
        await _unitOfWork.ReceivedWithAnyArgs(1).ExecuteInTransactionAsync(default!, Ct);
    }

    // ── Failed and uneconomical repairs (US3, T248; FR-029 – FR-032) ───────────────────────────

    [Fact]
    public async Task A_failure_below_the_attempt_limit_keeps_the_remedy_and_records_the_evaluation_and_the_ended_attempt()
    {
        var (claim, repair) = ArrangeAtRepairCentre();
        var remedy = _remedy.ShouldNotBeNull();

        var result = await Execute(claim, Failed());

        result.ShouldBeOfType<RecordRepairEventResult.Recorded>();
        (repair.Status, repair.NotPossibleReason, repair.FailedAttempts).ShouldBe(
            (RepairStatus.RepairNotPossible, (RepairNotPossibleReason?)RepairNotPossibleReason.Failed, 1));
        (remedy.State, remedy.Remedy).ShouldBe((RemedyState.InProgress, (RemedyType?)RemedyType.Repair));
        RecordRepairEvent.AllowedEvents(repair, remedy).ShouldContain(RepairEventType.NewAttemptStarted);

        var evaluated = _decisions.ShouldHaveSingleItem();
        (evaluated.Kind, evaluated.Scenario, evaluated.TableRow, evaluated.FailedAttempts, evaluated.PolicyVersionId).ShouldBe(
            (RemedyDecisionKind.Evaluated, RemedyScenario.RepairFailed, "repairFailed", 1, VersionId));
        var entry = _entries.ShouldHaveSingleItem();
        (entry.Step, entry.Actor).ShouldBe((TrailStep.RepairAttemptEnded, AgentSub));
        entry.Payload.GetProperty("policyCode").GetString().ShouldBe("AUR-WP");
        entry.Payload.GetProperty("newAttemptAllowed").GetBoolean().ShouldBeTrue();
        entry.Payload.GetProperty("failedAttempts").GetInt32().ShouldBe(1);
        await _unitOfWork.ReceivedWithAnyArgs(1).ExecuteInTransactionAsync(default!, Ct);
    }

    [Theory]
    [InlineData("AUR-WP", RemedyType.Replacement)]
    [InlineData("BOR-WP", RemedyType.Refund)]
    public async Task A_failure_at_the_attempt_limit_returns_the_remedy_to_a_reviewer_with_the_company_remedy(string policy, RemedyType expected)
    {
        UseRules(policy == "AUR-WP" ? RemedyRulesContract.Aurora : RemedyRulesContract.Borealis, policy);
        var (claim, repair) = ArrangeAtRepairCentre();
        repair.RecordFailure("The display flickers again after the panel was replaced.", Report(1));
        repair.StartNewAttempt(ProductLocation.AtCentre, Report(1));
        var remedy = _remedy.ShouldNotBeNull();

        var result = await Execute(claim, Failed());

        result.ShouldBeOfType<RecordRepairEventResult.Recorded>();
        repair.FailedAttempts.ShouldBe(2);
        (remedy.State, remedy.Scenario, remedy.PendingReason, remedy.Remedy).ShouldBe(
            (RemedyState.AwaitingDecision, RemedyScenario.RepairFailed, (RemedyPendingReason?)RemedyPendingReason.RepairFailedLimit, (RemedyType?)null));
        remedy.AllowedRemedies.ShouldBe([expected]);
        (_decisions.ShouldHaveSingleItem().Kind, _decisions[0].FailedAttempts).ShouldBe((RemedyDecisionKind.Evaluated, 2));
        var entry = _entries.ShouldHaveSingleItem();
        entry.Step.ShouldBe(TrailStep.RepairAttemptEnded);
        (entry.Payload.GetProperty("policyCode").GetString(), entry.Payload.GetProperty("recommendedRemedy").GetString())
            .ShouldBe((policy, expected.ToString()));
        entry.Payload.GetProperty("newAttemptAllowed").GetBoolean().ShouldBeFalse();

        // No new attempt can start: the remedy waits for the reviewer.
        var refused = (await Execute(claim, NewAttempt() with { ReportedAt = ReportedOn(3) }))
            .ShouldBeOfType<RecordRepairEventResult.Conflict>();
        refused.Status.ShouldBe(RepairStatus.RepairNotPossible);
        refused.AllowedEvents.ShouldNotContain(RepairEventType.NewAttemptStarted);
        repair.Status.ShouldBe(RepairStatus.RepairNotPossible);
    }

    [Fact]
    public async Task A_new_attempt_below_the_limit_opens_the_next_attempt_at_the_centre()
    {
        var (claim, repair) = ArrangeAtRepairCentre();
        repair.RecordFailure("The display flickers again after the panel was replaced.", Report(1));

        var result = await Execute(claim, NewAttempt());

        result.ShouldBeOfType<RecordRepairEventResult.Recorded>();
        repair.Status.ShouldBe(RepairStatus.AtRepairCentre);
        repair.CurrentAttempt.ShouldNotBeNull().Seq.ShouldBe(2);
        _entries.ShouldHaveSingleItem().Step.ShouldBe(TrailStep.RepairStatusUpdated);
        _decisions.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_estimate_at_the_economic_limit_continues_the_repair_without_an_evaluation()
    {
        UseRules(RemedyRulesContract.Borealis, "BOR-WP");
        var (claim, repair) = ArrangeAtRepairCentre();
        var limit = ClaimValue * 60 / 100m;

        var result = await Execute(claim, Estimate(limit));

        result.ShouldBeOfType<RecordRepairEventResult.Recorded>();
        (repair.Status, repair.CurrentAttempt.ShouldNotBeNull().EstimateAmount).ShouldBe((RepairStatus.AtRepairCentre, (decimal?)limit));
        _remedy.ShouldNotBeNull().State.ShouldBe(RemedyState.InProgress);
        _decisions.ShouldBeEmpty();
        _entries.ShouldHaveSingleItem().Step.ShouldBe(TrailStep.RepairStatusUpdated);
    }

    [Fact]
    public async Task An_estimate_above_the_economic_limit_offers_the_reviewer_a_choice_without_counting_a_failure()
    {
        UseRules(RemedyRulesContract.Borealis, "BOR-WP");
        var (claim, repair) = ArrangeAtRepairCentre();
        var above = (ClaimValue * 60 / 100m) + 0.01m;
        var remedy = _remedy.ShouldNotBeNull();

        var result = await Execute(claim, Estimate(above));

        result.ShouldBeOfType<RecordRepairEventResult.Recorded>();
        (repair.Status, repair.NotPossibleReason, repair.FailedAttempts).ShouldBe(
            (RepairStatus.RepairNotPossible, (RepairNotPossibleReason?)RepairNotPossibleReason.NotEconomical, 0));
        (remedy.State, remedy.Scenario, remedy.PendingReason).ShouldBe(
            (RemedyState.AwaitingDecision, RemedyScenario.RepairNotEconomical, (RemedyPendingReason?)RemedyPendingReason.RepairNotEconomical));
        remedy.AllowedRemedies.ShouldBe([RemedyType.Refund, RemedyType.Replacement], ignoreOrder: true);
        var evaluated = _decisions.ShouldHaveSingleItem();
        (evaluated.TableRow, evaluated.EstimateAmount, evaluated.FailedAttempts).ShouldBe(("repairNotEconomical", (decimal?)above, 0));
        _entries.ShouldHaveSingleItem().Step.ShouldBe(TrailStep.RepairAttemptEnded);
    }

    [Fact]
    public async Task Without_an_economic_limit_no_estimate_ends_the_repair()
    {
        var (claim, repair) = ArrangeAtRepairCentre();

        (await Execute(claim, Estimate(ClaimValue * 10))).ShouldBeOfType<RecordRepairEventResult.Recorded>();

        repair.Status.ShouldBe(RepairStatus.AtRepairCentre);
        _remedy.ShouldNotBeNull().State.ShouldBe(RemedyState.InProgress);
    }
    // ── Transfers and corrections (US5, T256; FR-026, FR-027) ──────────────────────────────────

    [Fact]
    public async Task A_transfer_to_an_eligible_centre_keeps_the_attempt_and_a_centre_of_another_tenant_is_not_found()
    {
        var (claim, repair) = ArrangeAtRepairCentre();
        var attempt = repair.CurrentAttempt.ShouldNotBeNull();

        (await Execute(claim, Transferred() with { RepairCentreId = ForeignCentre })).ShouldBeOfType<RecordRepairEventResult.NotFound>();
        await _securityEvents.Received(1).RecordAsync(
            SecurityEventKind.AccessDenied, AgentName, Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
        ShouldBeUnchanged(repair, RepairStatus.AtRepairCentre);

        (await Execute(claim, Transferred())).ShouldBeOfType<RecordRepairEventResult.Recorded>();
        repair.Status.ShouldBe(RepairStatus.AwaitingHandover);
        repair.ActiveHandover.ShouldNotBeNull().ServiceCenterId.ShouldBe(OtherCentre);
        (repair.CurrentAttempt, attempt.ServiceCenterId).ShouldBe((attempt, OtherCentre));
        var entry = _entries.ShouldHaveSingleItem();
        entry.Step.ShouldBe(TrailStep.RepairCentreAssigned);
        entry.Payload.GetProperty("reason").GetString().ShouldBe(Reason);
    }

    [Fact]
    public async Task A_transfer_to_the_active_centre_is_refused_naming_the_centre()
    {
        var (claim, repair) = ArrangeAtRepairCentre();

        ShouldBeInvalid(await Execute(claim, Transferred() with { RepairCentreId = SuggestedCentre }), "repairCentreId");
        ShouldBeUnchanged(repair, RepairStatus.AtRepairCentre);
    }

    [Fact]
    public async Task A_corrected_ready_mark_queues_no_notification_and_the_next_ready_mark_reuses_the_attempt_key()
    {
        var (claim, repair) = ArrangeAtRepairCentre();
        var attemptId = repair.CurrentAttempt.ShouldNotBeNull().Id;
        var marked = repair.MarkReady(Report(1));

        var result = await Execute(claim, Correction(marked.Seq) with { ToStatus = RepairStatus.AtRepairCentre });

        result.ShouldBeOfType<RecordRepairEventResult.Recorded>();
        repair.Status.ShouldBe(RepairStatus.AtRepairCentre);
        _notifications.ReceivedCalls().ShouldBeEmpty();
        var entry = _entries.ShouldHaveSingleItem();
        (entry.Step, entry.Payload.GetProperty("correctsSeq").GetInt32()).ShouldBe((TrailStep.RepairStatusUpdated, marked.Seq));

        (await Execute(claim, MarkedReady() with { ReportedAt = ReportedOn(3) })).ShouldBeOfType<RecordRepairEventResult.Recorded>();
        await _notifications.Received(1).EnqueueAsync(
            claim.Id, "RepairReadyForCollection", $"repair-ready:{attemptId}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_correction_naming_an_unknown_entry_or_a_value_that_does_not_fit_it_is_refused_naming_the_field()
    {
        var (claim, repair) = ArrangeAtRepairCentre();
        var handedOver = repair.Events.Single(e => e.Type == RepairEventType.HandedOver);

        ShouldBeInvalid(await Execute(claim, Correction(99) with { HandedOverOn = HandedOverOn }), "correctsSeq");
        ShouldBeInvalid(await Execute(claim, Correction(handedOver.Seq)), "correction");
        ShouldBeInvalid(await Execute(claim, Correction(handedOver.Seq) with { EstimateAmount = 10m }), "estimateAmount");
        ShouldBeUnchanged(repair, RepairStatus.AtRepairCentre);
    }

    [Fact]
    public async Task A_failure_that_sent_the_remedy_to_a_reviewer_cannot_be_sent_back()
    {
        var (claim, repair) = ArrangeAtRepairCentre();
        repair.RecordFailure("The display flickers again after the panel was replaced.", Report(1));
        repair.StartNewAttempt(ProductLocation.AtCentre, Report(1));
        (await Execute(claim, Failed())).ShouldBeOfType<RecordRepairEventResult.Recorded>();
        var failed = repair.Events[^1];
        _entries.Clear();

        var result = await Execute(claim, Correction(failed.Seq) with { ToStatus = RepairStatus.AtRepairCentre, ReportedAt = ReportedOn(3) });

        result.ShouldBeOfType<RecordRepairEventResult.Conflict>().Status.ShouldBe(RepairStatus.RepairNotPossible);
        (repair.Status, repair.FailedAttempts).ShouldBe((RepairStatus.RepairNotPossible, 2));
    }
    [Fact]
    public void The_application_registers_the_use_case_per_scope()
    {
        var services = new ServiceCollection();

        services.AddWarrantyApplication();

        services.Single(d => d.ServiceType == typeof(RecordRepairEvent)).Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private Task<RecordRepairEventResult> Execute(Claim claim, RecordRepairEventCommand command)
        => UseCase().ExecuteAsync(command with { ClaimId = claim.Id }, Ct);

    private RecordRepairEvent UseCase()
        => new(
            _tenant, _claims, _catalog, _remedies, _repairs, _policies, _network, new RemedyPolicyEvaluator(), _notifications, _trail,
            _securityEvents, _unitOfWork, _time);

    private static RecordRepairEventCommand Command(RepairEventType type, int day = 1)
        => new(Guid.Empty, ValidIfMatch, type, ReportedOn(day), ReportSource.Phone);

    private static RecordRepairEventCommand HandedOver()
        => Command(RepairEventType.HandedOver, day: 0) with { HandedOverOn = HandedOverOn, CentreReference = "JOB-1001" };

    private static RecordRepairEventCommand CentreChanged()
        => Command(RepairEventType.CentreChanged, day: -1) with { RepairCentreId = OtherCentre, Reason = Reason };

    private static RecordRepairEventCommand MarkedReady() => Command(RepairEventType.MarkedReady, day: 2);

    private static RecordRepairEventCommand Collected() => Command(RepairEventType.Collected, day: 3);

    private static RecordRepairEventCommand Estimate(decimal amount)
        => Command(RepairEventType.EstimateReported, day: 2) with { EstimateAmount = amount };

    private static RecordRepairEventCommand Failed()
        => Command(RepairEventType.RepairFailed, day: 2) with { Reason = "The fault came back after the board was replaced." };

    private static RecordRepairEventCommand NewAttempt()
        => Command(RepairEventType.NewAttemptStarted, day: 2) with { ProductLocation = ProductLocation.AtCentre };

    private static RecordRepairEventCommand Transferred()
        => Command(RepairEventType.Transferred, day: 2) with { RepairCentreId = OtherCentre, Reason = Reason };

    private static RecordRepairEventCommand Correction(int correctsSeq)
        => Command(RepairEventType.Correction, day: 2) with
        {
            CorrectsSeq = correctsSeq, Reason = "The entry was recorded by mistake; the centre confirmed it.",
        };
    /// <summary>The remedy's policy version (<see cref="VersionId"/>, policy <paramref name="code"/>) carries <paramref name="rules"/>.</summary>
    private void UseRules(RemedyRules rules, string code)
    {
        var terms = new CoverageTerms(
            new Dictionary<Region, int> { [Region.NA] = 12, [Region.EU] = 24 }, new Dictionary<string, int>(),
            AccidentalDamageTerms.NotCovered, [ExclusionCode.AccidentalDamage, ExclusionCode.LiquidDamage]);
        var version = PolicyVersion.Create(
            VersionId, Tenant, PolicyId, 2, new DateOnly(2025, 1, 1), null, [Region.NA, Region.EU], [], terms,
            $"tenant/policies/{code}-v2.md", "checksum", rules);
        _policies.GetVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(version);
        _policies.GetPolicyAsync(PolicyId, Arg.Any<CancellationToken>()).Returns(WarrantyPolicy.Create(PolicyId, Tenant, code, "Warranty"));
    }
    /// <summary>10:00 UTC, <paramref name="day"/> days after the hand-over date (all before <see cref="Now"/>).</summary>
    private static DateTimeOffset ReportedOn(int day)
        => new(HandedOverOn.AddDays(day).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);

    private static RepairReport Report(int day) => new(ReportedOn(day), ReportSource.Phone, AgentSub, null, ReportedOn(day).AddMinutes(5));

    private static Claim NewClaim(Guid? tenantId = null) => Claim.Submit(
        Guid.CreateVersion7(), tenantId ?? Tenant, ClaimReference.Generate(), ClaimChannel.AgentPortal, "agent-sub-submitter", CustomerId,
        "claimant@synthetic-mail.test", null, "AUR-TAB10", ProductId, "SN-2035", new DateOnly(2026, 1, 15),
        "Aurora Store", ClaimValue, Region.NA, "The tablet does not power on after charging overnight.", SubmittedAt);

    /// <summary>An approved claim with <paramref name="remedy"/> and <paramref name="repair"/> behind the repositories.</summary>
    private Claim ArrangeApproved(Claim claim, ClaimRemedy remedy, Repair? repair)
    {
        claim.StartEvaluation(SubmittedAt);
        claim.EscalateToReview(SubmittedAt);
        claim.FinalizeApproved("Your tablet is covered for manufacturing defects.", DecidedBy.Reviewer, ApprovedAt);

        _remedy = remedy;
        _claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);
        _remedies.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(remedy);
        _remedies.GetRowVersionAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(RemedyRowVersion);
        _repairs.GetByClaimAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(repair);
        _repairs.GetRowVersionAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(repair is null ? null : RowVersion);
        return claim;
    }

    /// <summary>Remedy Repair set by policy; the repair awaits the hand-over at the suggested centre.</summary>
    private (Claim Claim, Repair Repair) ArrangeAwaitingHandover()
    {
        var claim = NewClaim();
        var remedy = ClaimRemedy.SetByPolicy(
            Guid.CreateVersion7(), Tenant, claim.Id, VersionId, RemedyScenario.CoveredDefect, RemedyType.Repair, [RemedyType.Repair], ApprovedAt);
        var repair = Repair.Create(Guid.CreateVersion7(), Tenant, claim.Id, SuggestedCentre, ApprovedAt);
        ArrangeApproved(claim, remedy, repair);
        return (claim, repair);
    }

    /// <summary>As <see cref="ArrangeAwaitingHandover"/>, handed over on <see cref="HandedOverOn"/> (attempt 1 open).</summary>
    private (Claim Claim, Repair Repair) ArrangeAtRepairCentre()
    {
        var (claim, repair) = ArrangeAwaitingHandover();
        repair.RecordHandover(HandedOverOn, "JOB-1001", Report(0));
        return (claim, repair);
    }

    private List<RepairEvent> CaptureEvents()
    {
        var events = new List<RepairEvent>();
        _repairs.WhenForAnyArgs(r => r.AddEvent(default!)).Do(call => events.Add(call.Arg<RepairEvent>()));
        return events;
    }

    private static void ShouldBeInvalid(RecordRepairEventResult result, string field)
    {
        var invalid = result.ShouldBeOfType<RecordRepairEventResult.Invalid>();
        invalid.Errors.Keys.ShouldContain(field);
    }

    private void ShouldBeUnchanged(Repair repair, RepairStatus status)
    {
        repair.Status.ShouldBe(status);
        repair.Events.ShouldAllBe(e => e.RecordedAt < Now, "an applied event would be recorded at the current time");
        _repairs.DidNotReceiveWithAnyArgs().AddEvent(default!);
        _entries.ShouldBeEmpty();
        _notifications.ReceivedCalls().ShouldBeEmpty();
    }

    private sealed record TrailRecord(TrailStep Step, string Actor, JsonElement Payload);
}
