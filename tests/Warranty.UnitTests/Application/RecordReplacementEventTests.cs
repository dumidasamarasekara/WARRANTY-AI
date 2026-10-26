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
/// Staff replacement updates (003 T319; FR-009 – FR-022, research R6, R7, R10, R12): <see cref="RecordReplacementEvent"/>
/// refuses a <c>reportedAt</c> in the future or before the step it follows and missing event fields (400 naming the
/// field), a unit serial that is the claim's, was handed out before, is another claim's or is a catalog serial of another
/// model (400, FR-022), a hand-over under <see cref="ReplacementReturnRule.AtCollection"/> without the return confirmed
/// (400, FR-021), a location that is not an eligible replacement location of the tenant (404 + <c>ACCESS_DENIED</c>), an
/// auditor (403), a missing or stale replacement ETag (412) and an event the current status does not allow (409 naming the
/// status and the allowed events) — all without changing the replacement. <c>UnitReserved</c> queues exactly one
/// <c>ReplacementReadyForCollection</c> notification with the dedupe key <c>replacement-ready:{unitId}</c>;
/// <c>HandedToCustomer</c> completes the remedy and registers the handed-out serial with its cover end. Implemented in
/// T326 (on the domain transitions of T325, which merges first).
/// </summary>
public sealed class RecordReplacementEventTests
{
    private const string AgentSub = "agent-sub-t319";
    private const string AgentName = "Aurora Agent";
    private const uint RowVersion = 8312;
    private const uint RemedyRowVersion = 5150;
    private const string ValidIfMatch = "\"8312\"";
    private const string Reason = "The customer asked for a location closer to home.";
    private const string ClaimSerial = "SN-2035";
    private const string UnitSerial = "SN-AUR-TAB-9001";
    private const decimal ClaimValue = 349m;

    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OtherTenant = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid CustomerId = Guid.Parse("0199c000-0000-7000-8000-0000000003d7");
    private static readonly Guid ProductId = Guid.Parse("0199c000-0000-7000-8000-0000000003e7");
    private static readonly Guid OtherProductId = Guid.Parse("0199c000-0000-7000-8000-0000000003e8");
    private static readonly Guid PolicyId = Guid.Parse("0199c000-0000-7000-8000-0000000003a1");
    private static readonly Guid VersionId = Guid.Parse("0199c000-0000-7000-8000-0000000003b2");
    private static readonly Guid SuggestedLocation = Guid.Parse("33333333-3333-7333-8333-333333333331");
    private static readonly Guid OtherLocation = Guid.Parse("33333333-3333-7333-8333-333333333332");

    /// <summary>An active centre of the tenant that repairs but issues no replacements: not a replacement location.</summary>
    private static readonly Guid RepairOnlyCentre = Guid.Parse("33333333-3333-7333-8333-333333333339");

    /// <summary>An active location of the other tenant: never in this tenant's eligible list.</summary>
    private static readonly Guid ForeignLocation = Guid.Parse("44444444-4444-7444-8444-444444444441");

    private static readonly DateOnly PurchaseDate = new(2026, 1, 15);
    private static readonly DateTimeOffset SubmittedAt = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>When the remedy became Replacement and the replacement was created (its <c>LocationSuggested</c> event).</summary>
    private static readonly DateTimeOffset ApprovedAt = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly FirstDay = new(2026, 9, 4);
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 14, 30, 0, TimeSpan.Zero);

    /// <summary>The claim's coverage end: purchase date + the 12 standard months of <see cref="Region.NA"/>.</summary>
    private static readonly DateOnly OriginalCoverageEnd = PurchaseDate.AddMonths(12);

    private readonly ITenantContext _tenant = Substitute.For<ITenantContext>();
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly ICatalogRepository _catalog = Substitute.For<ICatalogRepository>();
    private readonly IRemedyRepository _remedies = Substitute.For<IRemedyRepository>();
    private readonly IReplacementRepository _replacements = Substitute.For<IReplacementRepository>();
    private readonly IReplacementSerialRegistry _serials = Substitute.For<IReplacementSerialRegistry>();
    private readonly IPolicyRepository _policies = Substitute.For<IPolicyRepository>();
    private readonly IServiceNetwork _network = Substitute.For<IServiceNetwork>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IDecisionTrailWriter _trail = Substitute.For<IDecisionTrailWriter>();
    private readonly ISecurityEventWriter _securityEvents = Substitute.For<ISecurityEventWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly List<TrailRecord> _entries = [];
    private readonly List<RegisteredReplacementSerial> _registered = [];
    private ClaimRemedy? _remedy;

    public RecordReplacementEventTests()
    {
        _tenant.IsResolved.Returns(true);
        _tenant.TenantId.Returns(Tenant);
        _tenant.PrincipalId.Returns(AgentSub);
        _tenant.PrincipalName.Returns(AgentName);
        _tenant.IsSystem.Returns(false);
        UseRole(Principals.ClaimsAgentRole);

        _unitOfWork.ExecuteInTransactionAsync(default!, default)
            .ReturnsForAnyArgs(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        _trail.WhenForAnyArgs(t => t.AppendAsync(default, default, default!, default!, default, default))
            .Do(call => _entries.Add(new TrailRecord(
                call.ArgAt<TrailStep>(1), call.ArgAt<string>(2), JsonSerializer.SerializeToElement(call.ArgAt<object?>(4)))));
        _serials.WhenForAnyArgs(s => s.Register(default!)).Do(call => _registered.Add(call.Arg<RegisteredReplacementSerial>()));
        _catalog.GetProductAsync(ProductId, Arg.Any<CancellationToken>())
            .Returns(Product.Create(ProductId, Tenant, "AUR-TAB10", "Aurora Tab 10", "tablet", ClaimValue, "USD"));
        _catalog.GetProductAsync(OtherProductId, Arg.Any<CancellationToken>())
            .Returns(Product.Create(OtherProductId, Tenant, "AUR-TAB11", "Aurora Tab 11", "tablet", 520m, "USD"));
        _network.ListEligibleAsync(Region.NA, "tablet", ServiceCenterPurpose.Replacement, Arg.Any<CancellationToken>()).Returns(
        [
            new EligibleServiceCenter(SuggestedLocation, "Aurora Service Center Springfield", Region.NA, "1 Main St", "Mon-Fri 9-17", "555-0100", 10, true),
            new EligibleServiceCenter(OtherLocation, "Aurora Service Center Shelbyville", Region.NA, "2 Oak Ave", "Mon-Sat 9-18", "555-0101", 20, true),
        ]);
        _network.ListEligibleAsync(Region.NA, "tablet", ServiceCenterPurpose.Repair, Arg.Any<CancellationToken>()).Returns(
        [
            new EligibleServiceCenter(SuggestedLocation, "Aurora Service Center Springfield", Region.NA, "1 Main St", "Mon-Fri 9-17", "555-0100", 10, true),
            new EligibleServiceCenter(OtherLocation, "Aurora Service Center Shelbyville", Region.NA, "2 Oak Ave", "Mon-Sat 9-18", "555-0101", 20, true),
            new EligibleServiceCenter(RepairOnlyCentre, "Aurora Repair Workshop", Region.NA, "3 Elm Rd", "Mon-Fri 8-16", "555-0102", 5, true),
        ]);
        _notifications.EnqueueAsync(default, default!, default!, default)
            .ReturnsForAnyArgs(NotificationEnqueueResult.Queued(Guid.Parse("0199c000-0000-7000-8000-0000000003f1")));
        UsePolicyVersion();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Reported time (FR-015) ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reportedAt_in_the_future_is_refused()
    {
        var (claim, replacement) = ArrangeAwaitingUnit();

        var result = await Execute(claim, UnitReserved() with { ReportedAt = Now.AddMinutes(5) });

        ShouldBeInvalid(result, "reportedAt");
        ShouldBeUnchanged(replacement, ReplacementStatus.AwaitingUnit);
    }

    [Fact]
    public async Task A_reportedAt_before_the_replacement_was_set_up_is_refused()
    {
        var (claim, replacement) = ArrangeAwaitingUnit();

        var result = await Execute(claim, UnitReserved() with { ReportedAt = ApprovedAt.AddHours(-1) });

        ShouldBeInvalid(result, "reportedAt");
        ShouldBeUnchanged(replacement, ReplacementStatus.AwaitingUnit);
    }

    [Fact]
    public async Task A_reportedAt_before_the_step_it_follows_is_refused()
    {
        var (claim, replacement) = ArrangeReadyForCollection();

        // The unit was reserved on day 1; the hand-over cannot have been reported on day 0.
        var result = await Execute(claim, HandedToCustomer() with { ReportedAt = ReportedOn(0) });

        ShouldBeInvalid(result, "reportedAt");
        ShouldBeUnchanged(replacement, ReplacementStatus.ReadyForCollection);
    }

    [Fact]
    public async Task A_reportedAt_at_the_step_it_follows_or_now_is_accepted()
    {
        var (claim, _) = ArrangeReadyForCollection();
        (await Execute(claim, HandedToCustomer() with { ReportedAt = ReportedOn(1) }))
            .ShouldBeOfType<RecordReplacementEventResult.Recorded>();

        var (other, _) = ArrangeReadyForCollection();
        (await Execute(other, HandedToCustomer() with { ReportedAt = Now }))
            .ShouldBeOfType<RecordReplacementEventResult.Recorded>();
    }

    // ── Event fields (contracts ReplacementEventRequest) ───────────────────────────────────────

    public static TheoryData<string, string> MissingOrInvalidFields => new()
    {
        { "noType", "type" },
        { "noReportedAt", "reportedAt" },
        { "noSource", "source" },
        { "systemSource", "source" },
        { "noteTooLong", "note" },
        { "locationChangeWithoutLocation", "locationId" },
        { "locationChangeWithoutReason", "reason" },
        { "locationChangeWithShortReason", "reason" },
        { "locationChangeWithLongReason", "reason" },
        { "unitWithoutSerial", "serialNumber" },
        { "unitWithBlankSerial", "serialNumber" },
        { "unitWithLongSerial", "serialNumber" },
        { "returnWithoutDate", "returnedOn" },
        { "returnInTheFuture", "returnedOn" },
        { "returnWithoutSerialSeen", "serialSeen" },
        { "handOverWithoutDate", "handedOverOn" },
        { "handOverInTheFuture", "handedOverOn" },
    };

    [Theory]
    [MemberData(nameof(MissingOrInvalidFields))]
    public async Task Missing_or_invalid_event_fields_are_refused_naming_the_field(string request, string field)
    {
        var (claim, replacement) = ArrangeAwaitingUnit();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        var command = request switch
        {
            "noType" => UnitReserved() with { Type = null },
            "noReportedAt" => UnitReserved() with { ReportedAt = null },
            "noSource" => UnitReserved() with { Source = null },
            "systemSource" => UnitReserved() with { Source = ReportSource.System },
            "noteTooLong" => UnitReserved() with { Note = new string('n', RecordReplacementEvent.MaxNoteLength + 1) },
            "locationChangeWithoutLocation" => LocationChanged() with { LocationId = null },
            "locationChangeWithoutReason" => LocationChanged() with { Reason = null },
            "locationChangeWithShortReason" => LocationChanged() with { Reason = "too short" },
            "locationChangeWithLongReason" => LocationChanged() with { Reason = new string('r', RecordReplacementEvent.MaxReasonLength + 1) },
            "unitWithoutSerial" => UnitReserved() with { SerialNumber = null },
            "unitWithBlankSerial" => UnitReserved() with { SerialNumber = "   " },
            "unitWithLongSerial" => UnitReserved() with { SerialNumber = new string('S', RecordReplacementEvent.MaxSerialLength + 1) },
            "returnWithoutDate" => ReturnRecorded() with { ReturnedOn = null },
            "returnInTheFuture" => ReturnRecorded() with { ReturnedOn = today.AddDays(1) },
            "returnWithoutSerialSeen" => ReturnRecorded() with { SerialSeen = null },
            "handOverWithoutDate" => HandedToCustomer() with { HandedOverOn = null },
            "handOverInTheFuture" => HandedToCustomer() with { HandedOverOn = today.AddDays(1) },
            _ => throw new ArgumentOutOfRangeException(nameof(request), request, null),
        };

        var result = await Execute(claim, command);

        ShouldBeInvalid(result, field);
        ShouldBeUnchanged(replacement, ReplacementStatus.AwaitingUnit);
    }

    // ── Serial rules of a reserved unit (FR-022) ───────────────────────────────────────────────

    [Theory]
    [InlineData(ClaimSerial)]
    [InlineData(" sn-2035 ")]
    public async Task A_unit_serial_equal_to_the_claims_serial_is_refused(string serial)
    {
        var (claim, replacement) = ArrangeAwaitingUnit();

        var result = await Execute(claim, UnitReserved() with { SerialNumber = serial });

        ShouldBeInvalid(result, "serialNumber");
        ShouldBeUnchanged(replacement, ReplacementStatus.AwaitingUnit);
    }

    [Fact]
    public async Task A_unit_serial_already_handed_out_or_of_another_claim_is_refused()
    {
        var (claim, replacement) = ArrangeAwaitingUnit();
        _serials.IsSerialInUseAsync(UnitSerial, Arg.Any<CancellationToken>()).Returns(true);

        var result = await Execute(claim, UnitReserved());

        ShouldBeInvalid(result, "serialNumber");
        ShouldBeUnchanged(replacement, ReplacementStatus.AwaitingUnit);
    }

    [Fact]
    public async Task A_catalog_serial_of_another_model_is_refused()
    {
        var (claim, replacement) = ArrangeAwaitingUnit();
        _catalog.FindSerialAsync(UnitSerial, Arg.Any<CancellationToken>()).Returns(ProductSerial.Create(Tenant, UnitSerial, OtherProductId));

        var result = await Execute(claim, UnitReserved());

        ShouldBeInvalid(result, "serialNumber");
        ShouldBeUnchanged(replacement, ReplacementStatus.AwaitingUnit);
    }

    [Fact]
    public async Task A_catalog_serial_of_the_claimed_model_is_accepted()
    {
        var (claim, replacement) = ArrangeAwaitingUnit();
        _catalog.FindSerialAsync(UnitSerial, Arg.Any<CancellationToken>()).Returns(ProductSerial.Create(Tenant, UnitSerial, ProductId));

        var result = await Execute(claim, UnitReserved());

        result.ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        var unit = replacement.CurrentUnit.ShouldNotBeNull();
        (unit.ProductId, unit.SerialNumber).ShouldBe((ProductId, UnitSerial));
    }

    // ── Return confirmation at collection (FR-021) ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task A_hand_over_at_collection_without_the_return_confirmed_is_refused(bool? returnConfirmed)
    {
        var (claim, replacement) = ArrangeReadyForCollection();

        var result = await Execute(claim, HandedToCustomer() with { ReturnConfirmed = returnConfirmed });

        ShouldBeInvalid(result, "returnConfirmed");
        ShouldBeUnchanged(replacement, ReplacementStatus.ReadyForCollection);
        _remedy.ShouldNotBeNull().State.ShouldBe(RemedyState.InProgress);
    }

    [Fact]
    public async Task A_hand_over_after_the_return_was_recorded_needs_no_confirmation()
    {
        var (claim, replacement) = ArrangeReadyForCollection();
        replacement.RecordReturn(FirstDay.AddDays(1), ClaimSerial, serialMatches: true, Report(1));

        var result = await Execute(claim, HandedToCustomer() with { ReturnConfirmed = null });

        result.ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.HandedToCustomer, ReturnState.Returned));
    }

    // ── Roles (FR-016) ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_auditor_records_nothing()
    {
        UseRole(Principals.AuditorRole);
        var (claim, replacement) = ArrangeAwaitingUnit();

        var result = await Execute(claim, UnitReserved());

        result.ShouldBeOfType<RecordReplacementEventResult.Forbidden>();
        ShouldBeUnchanged(replacement, ReplacementStatus.AwaitingUnit);
        await _unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!, Ct);
    }

    [Theory]
    [InlineData(Principals.ClaimsAgentRole)]
    [InlineData(Principals.ClaimsReviewerRole)]
    public async Task Claims_agents_and_reviewers_record_replacement_updates(string role)
    {
        UseRole(role);
        var (claim, replacement) = ArrangeAwaitingUnit();

        (await Execute(claim, UnitReserved())).ShouldBeOfType<RecordReplacementEventResult.Recorded>();

        replacement.Status.ShouldBe(ReplacementStatus.ReadyForCollection);
    }

    // ── Tenant isolation (FR-011) ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_location_of_another_tenant_is_not_found_and_records_ACCESS_DENIED()
    {
        var (claim, replacement) = ArrangeAwaitingUnit();

        var result = await Execute(claim, LocationChanged() with { LocationId = ForeignLocation });

        result.ShouldBeOfType<RecordReplacementEventResult.NotFound>();
        await _securityEvents.Received(1).RecordAsync(
            SecurityEventKind.AccessDenied, AgentName, Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
        ShouldBeUnchanged(replacement, ReplacementStatus.AwaitingUnit);
        replacement.ActiveLocation.ShouldNotBeNull().ServiceCenterId.ShouldBe(SuggestedLocation);
    }

    [Fact]
    public async Task A_centre_that_issues_no_replacements_is_not_an_eligible_location()
    {
        var (claim, replacement) = ArrangeAwaitingUnit();

        var result = await Execute(claim, LocationChanged() with { LocationId = RepairOnlyCentre });

        result.ShouldBeOfType<RecordReplacementEventResult.NotFound>();
        await _securityEvents.Received(1).RecordAsync(
            SecurityEventKind.AccessDenied, AgentName, Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
        ShouldBeUnchanged(replacement, ReplacementStatus.AwaitingUnit);
    }

    [Fact]
    public async Task An_unknown_claim_or_a_claim_of_another_tenant_is_not_found()
    {
        (await UseCase().ExecuteAsync(UnitReserved() with { ClaimId = Guid.NewGuid() }, Ct))
            .ShouldBeOfType<RecordReplacementEventResult.NotFound>();

        var foreign = NewClaim(OtherTenant);
        _claims.GetAsync(foreign.Id, Arg.Any<CancellationToken>()).Returns(foreign);
        (await Execute(foreign, UnitReserved())).ShouldBeOfType<RecordReplacementEventResult.NotFound>();

        await _unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!, Ct);
        _notifications.ReceivedCalls().ShouldBeEmpty();
    }

    // ── Concurrency (FR-018) ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("W/\"8312\"")]
    [InlineData("\"8311\"")]
    [InlineData("not-a-version")]
    public async Task A_missing_or_stale_If_Match_fails_the_precondition(string? ifMatch)
    {
        var (claim, replacement) = ArrangeAwaitingUnit();

        var result = await Execute(claim, UnitReserved() with { IfMatch = ifMatch });

        result.ShouldBeOfType<RecordReplacementEventResult.PreconditionFailed>();
        ShouldBeUnchanged(replacement, ReplacementStatus.AwaitingUnit);
    }

    [Fact]
    public async Task The_remedy_ETag_is_not_the_replacement_ETag()
    {
        var (claim, replacement) = ArrangeAwaitingUnit();

        var result = await Execute(claim, UnitReserved() with { IfMatch = $"\"{RemedyRowVersion}\"" });

        result.ShouldBeOfType<RecordReplacementEventResult.PreconditionFailed>();
        ShouldBeUnchanged(replacement, ReplacementStatus.AwaitingUnit);
    }

    [Fact]
    public async Task A_concurrent_change_detected_on_save_conflicts()
    {
        var (claim, _) = ArrangeAwaitingUnit();
        _unitOfWork.Configure().ExecuteInTransactionAsync(default!, Ct).ThrowsAsyncForAnyArgs(new ConcurrencyConflictException("xmin changed"));

        var result = await Execute(claim, UnitReserved());

        result.ShouldBeOfType<RecordReplacementEventResult.Conflict>().Detail.ShouldBe(RecordReplacementEvent.ConcurrentChangeDetail);
    }

    // ── Refused transitions (FR-012 – FR-014) ──────────────────────────────────────────────────

    [Fact]
    public async Task An_event_the_status_does_not_allow_conflicts_naming_the_status_and_the_allowed_events()
    {
        var (claim, replacement) = ArrangeAwaitingUnit();

        var result = await Execute(claim, HandedToCustomer());

        var conflict = result.ShouldBeOfType<RecordReplacementEventResult.Conflict>();
        conflict.Status.ShouldBe(ReplacementStatus.AwaitingUnit);
        conflict.AllowedEvents.ShouldBe(replacement.AllowedEvents(StaffRole.ClaimsAgent), ignoreOrder: true);
        conflict.AllowedEvents.ShouldContain(ReplacementEventType.UnitReserved);
        conflict.AllowedEvents.ShouldNotContain(ReplacementEventType.HandedToCustomer);
        conflict.Detail.ShouldContain(nameof(ReplacementStatus.AwaitingUnit));
        conflict.Detail.ShouldContain(nameof(ReplacementEventType.UnitReserved));
        ShouldBeUnchanged(replacement, ReplacementStatus.AwaitingUnit);
    }

    [Fact]
    public async Task A_claim_without_a_replacement_conflicts()
    {
        var claim = NewClaim();
        ArrangeApproved(claim, ReplacementRemedy(claim), replacement: null);

        var result = await Execute(claim, UnitReserved());

        result.ShouldBeOfType<RecordReplacementEventResult.Conflict>().Detail.ShouldBe(RecordReplacementEvent.NoReplacementDetail);
        await _unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!, Ct);
    }

    // ── Recorded events ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_location_change_to_an_eligible_location_is_recorded_with_one_event_and_one_trail_entry_in_one_transaction()
    {
        var (claim, replacement) = ArrangeAwaitingUnit();
        var events = CaptureEvents();

        var result = await Execute(claim, LocationChanged());

        result.ShouldBeOfType<RecordReplacementEventResult.Recorded>().Replacement.ShouldBeSameAs(replacement);
        replacement.Status.ShouldBe(ReplacementStatus.AwaitingUnit);
        var active = replacement.ActiveLocation.ShouldNotBeNull();
        (active.ServiceCenterId, active.Assignment, active.Reason).ShouldBe((OtherLocation, LocationAssignment.ChosenByStaff, Reason));
        var recorded = events.ShouldHaveSingleItem();
        (recorded.Type, recorded.Actor, recorded.Source, recorded.RecordedAt).ShouldBe(
            (ReplacementEventType.LocationChanged, AgentSub, ReportSource.Phone, Now));
        recorded.ReportedAt.ShouldBe(LocationChanged().ReportedAt!.Value);
        var entry = _entries.ShouldHaveSingleItem();
        (entry.Step, entry.Actor).ShouldBe((TrailStep.ReplacementLocationAssigned, AgentSub));
        await _unitOfWork.ReceivedWithAnyArgs(1).ExecuteInTransactionAsync(default!, Ct);
        await _securityEvents.DidNotReceiveWithAnyArgs().RecordAsync(default, default!, default, default, Ct);
        _notifications.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Reserving_a_unit_queues_one_ready_notification_keyed_by_the_unit()
    {
        var (claim, replacement) = ArrangeAwaitingUnit();
        var events = CaptureEvents();

        var result = await Execute(claim, UnitReserved() with { SerialNumber = " sn-aur-tab-9001 " });

        result.ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        replacement.Status.ShouldBe(ReplacementStatus.ReadyForCollection);
        var unit = replacement.CurrentUnit.ShouldNotBeNull();
        (unit.ProductId, unit.SerialNumber, unit.ServiceCenterId, unit.Result).ShouldBe(
            (ProductId, UnitSerial, SuggestedLocation, ReplacementUnitResult.Reserved));
        await _notifications.Received(1).EnqueueAsync(
            claim.Id, "ReplacementReadyForCollection", $"replacement-ready:{unit.Id}", Arg.Any<CancellationToken>());
        _notifications.ReceivedCalls().Count().ShouldBe(1, "only the keyed overload, once");
        RecordReplacementEvent.ReadyDedupeKey(unit.Id).ShouldBe($"replacement-ready:{unit.Id}");
        events.ShouldHaveSingleItem().Type.ShouldBe(ReplacementEventType.UnitReserved);
        _entries.ShouldHaveSingleItem().Step.ShouldBe(TrailStep.ReplacementStatusUpdated);
        await _unitOfWork.ReceivedWithAnyArgs(1).ExecuteInTransactionAsync(default!, Ct);
        _registered.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_ready_notification_already_queued_for_the_unit_still_records_the_event()
    {
        var (claim, replacement) = ArrangeAwaitingUnit();
        _notifications.EnqueueAsync(default, default!, default!, Ct).ReturnsForAnyArgs(NotificationEnqueueResult.Duplicate);

        var result = await Execute(claim, UnitReserved());

        result.ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        replacement.Status.ShouldBe(ReplacementStatus.ReadyForCollection);
    }

    [Fact]
    public async Task A_return_recorded_at_collection_keeps_the_status_and_records_the_return()
    {
        var (claim, replacement) = ArrangeAwaitingUnit();
        var events = CaptureEvents();

        var result = await Execute(claim, ReturnRecorded());

        result.ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingUnit, ReturnState.Returned));
        var recorded = replacement.Return.ShouldNotBeNull();
        (recorded.ReturnedOn, recorded.SerialMatches).ShouldBe((FirstDay.AddDays(1), true));
        events.ShouldHaveSingleItem().Type.ShouldBe(ReplacementEventType.ReturnRecorded);
        _entries.ShouldHaveSingleItem().Step.ShouldBe(TrailStep.FaultyProductReturned);
        _notifications.ReceivedCalls().ShouldBeEmpty();
        await _unitOfWork.ReceivedWithAnyArgs(1).ExecuteInTransactionAsync(default!, Ct);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task The_hand_over_completes_the_remedy_and_registers_the_serial_with_its_cover_end(int minimumCoverMonths)
    {
        var (claim, replacement) = ArrangeReadyForCollection(minimumCoverMonths);
        var remedy = _remedy.ShouldNotBeNull();
        var handedOverOn = FirstDay.AddDays(2);
        var events = CaptureEvents();

        var result = await Execute(claim, HandedToCustomer());

        result.ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.HandedToCustomer, ReturnState.Returned));
        replacement.CurrentUnit.ShouldNotBeNull().Result.ShouldBe(ReplacementUnitResult.HandedOver);
        (remedy.State, remedy.Remedy).ShouldBe((RemedyState.Completed, (RemedyType?)RemedyType.Replacement));

        var registered = _registered.ShouldHaveSingleItem();
        (registered.TenantId, registered.SerialNumber, registered.ProductId, registered.OriginalSerial, registered.ClaimId)
            .ShouldBe((Tenant, UnitSerial, ProductId, ClaimSerial, claim.Id));
        (registered.OriginalPurchaseDate, registered.HandedOverOn).ShouldBe((PurchaseDate, handedOverOn));
        var expectedCoverEnd = minimumCoverMonths == 0 ? OriginalCoverageEnd : handedOverOn.AddMonths(minimumCoverMonths);
        registered.CoverEndDate.ShouldBe(expectedCoverEnd);
        registered.CoverEndDate.ShouldBe(RegisteredReplacementSerial.CoverEndOf(OriginalCoverageEnd, handedOverOn, minimumCoverMonths));

        // The handed-out serial is not in the catalog yet: it is added for the replacement product (research R10).
        _catalog.Received(1).AddSerial(Arg.Is<ProductSerial>(s =>
            s.TenantId == Tenant && s.SerialNumber == UnitSerial && s.ProductId == ProductId));

        events.ShouldHaveSingleItem().Type.ShouldBe(ReplacementEventType.HandedToCustomer);
        _entries.Select(e => e.Step).ShouldContain(TrailStep.RemedyCompleted);
        _entries.Select(e => e.Step).ShouldContain(TrailStep.ReplacementUnitRegistered);
        _entries.ShouldAllBe(e => e.Actor == AgentSub);
        _notifications.ReceivedCalls().ShouldBeEmpty();
        await _unitOfWork.ReceivedWithAnyArgs(1).ExecuteInTransactionAsync(default!, Ct);
    }

    [Fact]
    public async Task A_handed_out_serial_already_in_the_catalog_is_registered_but_not_added_to_the_catalog_again()
    {
        var (claim, _) = ArrangeReadyForCollection();
        _catalog.FindSerialAsync(UnitSerial, Arg.Any<CancellationToken>()).Returns(ProductSerial.Create(Tenant, UnitSerial, ProductId));

        var result = await Execute(claim, HandedToCustomer());

        result.ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        _registered.ShouldHaveSingleItem().SerialNumber.ShouldBe(UnitSerial);
        _catalog.DidNotReceiveWithAnyArgs().AddSerial(default!);
    }

    [Fact]
    public void The_application_registers_the_use_case_per_scope()
    {
        var services = new ServiceCollection();

        services.AddWarrantyApplication();

        services.Single(d => d.ServiceType == typeof(RecordReplacementEvent)).Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private Task<RecordReplacementEventResult> Execute(Claim claim, RecordReplacementEventCommand command)
        => UseCase().ExecuteAsync(command with { ClaimId = claim.Id }, Ct);

    private RecordReplacementEvent UseCase()
        => new(
            _tenant, _claims, _catalog, _remedies, _replacements, _serials, _policies, _network, new RemedyPolicyEvaluator(),
                new ReplacementPolicy(), _notifications, _trail,
            _securityEvents, _unitOfWork, _time);

    private void UseRole(string role) => _tenant.Roles.Returns(new HashSet<string>(StringComparer.Ordinal) { role });

    private static RecordReplacementEventCommand Command(ReplacementEventType type, int day = 1)
        => new(Guid.Empty, ValidIfMatch, type, ReportedOn(day), ReportSource.Phone);

    private static RecordReplacementEventCommand LocationChanged()
        => Command(ReplacementEventType.LocationChanged, day: 0) with { LocationId = OtherLocation, Reason = Reason };

    private static RecordReplacementEventCommand ReturnRecorded()
        => Command(ReplacementEventType.ReturnRecorded, day: 1) with { ReturnedOn = FirstDay.AddDays(1), SerialSeen = "sn-2035" };

    private static RecordReplacementEventCommand UnitReserved()
        => Command(ReplacementEventType.UnitReserved, day: 1) with { SerialNumber = UnitSerial };

    private static RecordReplacementEventCommand HandedToCustomer()
        => Command(ReplacementEventType.HandedToCustomer, day: 2) with { HandedOverOn = FirstDay.AddDays(2), ReturnConfirmed = true };

    /// <summary>The remedy's policy version (<see cref="VersionId"/>): 12 standard months in <see cref="Region.NA"/>.</summary>
    private void UsePolicyVersion()
    {
        var terms = new CoverageTerms(
            new Dictionary<Region, int> { [Region.NA] = 12, [Region.EU] = 24 }, new Dictionary<string, int>(),
            AccidentalDamageTerms.NotCovered, [ExclusionCode.AccidentalDamage, ExclusionCode.LiquidDamage]);
        var version = PolicyVersion.Create(
            VersionId, Tenant, PolicyId, 2, new DateOnly(2025, 1, 1), null, [Region.NA, Region.EU], [], terms,
            "tenant/policies/AUR-WP-v2.md", "checksum", RemedyRulesContract.AuroraWithReplacement);
        _policies.GetVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(version);
        _policies.GetPolicyAsync(PolicyId, Arg.Any<CancellationToken>()).Returns(WarrantyPolicy.Create(PolicyId, Tenant, "AUR-WP", "Warranty"));
    }

    /// <summary>10:00 UTC, <paramref name="day"/> days after <see cref="FirstDay"/> (all after the set-up and before <see cref="Now"/>).</summary>
    private static DateTimeOffset ReportedOn(int day)
        => new(FirstDay.AddDays(day).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);

    private static ReplacementReport Report(int day)
        => new(ReportedOn(day), ReportSource.Phone, AgentSub, StaffRole.ClaimsAgent, null, ReportedOn(day).AddMinutes(5));

    private static Claim NewClaim(Guid? tenantId = null) => Claim.Submit(
        Guid.CreateVersion7(), tenantId ?? Tenant, ClaimReference.Generate(), ClaimChannel.AgentPortal, "agent-sub-submitter", CustomerId,
        "claimant@synthetic-mail.test", null, "AUR-TAB10", ProductId, ClaimSerial, PurchaseDate,
        "Aurora Store", ClaimValue, Region.NA, "The tablet does not power on after charging overnight.", SubmittedAt);

    private static ClaimRemedy ReplacementRemedy(Claim claim) => ClaimRemedy.SetByPolicy(
        Guid.CreateVersion7(), Tenant, claim.Id, VersionId, RemedyScenario.CoveredDefect, RemedyType.Replacement,
        [RemedyType.Replacement], ApprovedAt);

    /// <summary>An approved claim with <paramref name="remedy"/> and <paramref name="replacement"/> behind the repositories.</summary>
    private Claim ArrangeApproved(Claim claim, ClaimRemedy remedy, Replacement? replacement)
    {
        claim.StartEvaluation(SubmittedAt);
        claim.EscalateToReview(SubmittedAt);
        claim.FinalizeApproved("Your tablet is covered for manufacturing defects.", DecidedBy.Reviewer, ApprovedAt);

        _remedy = remedy;
        _claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);
        _remedies.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(remedy);
        _remedies.GetRowVersionAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(RemedyRowVersion);
        _replacements.GetByClaimAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(replacement);
        _replacements.GetRowVersionAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(replacement is null ? null : RowVersion);
        return claim;
    }

    /// <summary>
    /// Remedy Replacement set by policy (same model, returned at collection); the replacement awaits a unit at the suggested
    /// location.
    /// </summary>
    private (Claim Claim, Replacement Replacement) ArrangeAwaitingUnit(int minimumCoverMonths = 0)
    {
        var claim = NewClaim();
        var replacement = Replacement.Create(
            Guid.CreateVersion7(), Tenant, claim.Id, VersionId, ReplacementProductRule.SameModel, ReplacementReturnRule.AtCollection,
            minimumCoverMonths, ReplacementStatus.AwaitingUnit, SuggestedLocation, ApprovedAt);
        ArrangeApproved(claim, ReplacementRemedy(claim), replacement);
        return (claim, replacement);
    }

    /// <summary>As <see cref="ArrangeAwaitingUnit"/>, with unit <see cref="UnitSerial"/> reserved on day 1 (return not yet recorded).</summary>
    private (Claim Claim, Replacement Replacement) ArrangeReadyForCollection(int minimumCoverMonths = 0)
    {
        var (claim, replacement) = ArrangeAwaitingUnit(minimumCoverMonths);
        replacement.ReserveUnit(ProductId, UnitSerial, Report(1));
        return (claim, replacement);
    }

    private List<ReplacementEvent> CaptureEvents()
    {
        var events = new List<ReplacementEvent>();
        _replacements.WhenForAnyArgs(r => r.AddEvent(default!)).Do(call => events.Add(call.Arg<ReplacementEvent>()));
        return events;
    }

    private static void ShouldBeInvalid(RecordReplacementEventResult result, string field)
    {
        var invalid = result.ShouldBeOfType<RecordReplacementEventResult.Invalid>();
        invalid.Errors.Keys.ShouldContain(field);
    }

    private void ShouldBeUnchanged(Replacement replacement, ReplacementStatus status)
    {
        replacement.Status.ShouldBe(status);
        replacement.Events.ShouldAllBe(e => e.RecordedAt < Now, "an applied event would be recorded at the current time");
        _replacements.DidNotReceiveWithAnyArgs().AddEvent(default!);
        _entries.ShouldBeEmpty();
        _notifications.ReceivedCalls().ShouldBeEmpty();
        _registered.ShouldBeEmpty();
    }

    private sealed record TrailRecord(TrailStep Step, string Actor, JsonElement Payload);
}
