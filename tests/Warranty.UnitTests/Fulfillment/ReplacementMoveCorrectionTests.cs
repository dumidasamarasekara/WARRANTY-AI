using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Fulfillment;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Guardrails.Remedies;

namespace Warranty.UnitTests.Fulfillment;

/// <summary>
/// Moves, refused units and corrections of a replacement (003 T353; User Story 5, FR-009, FR-010, FR-014, FR-017, FR-027,
/// FR-031, research R7; quickstart X16, X17). A move leaves exactly one active location (the old one and any unit reserved there
/// stay in the history, the unit <see cref="ReplacementUnitResult.Released"/>) and a ready replacement awaits a new unit; a
/// refused unit stays in the history with its reason, the replacement awaits a new unit and the remedy does not change; every
/// newly reserved unit is a new unit with its own ready notification (<c>replacement-ready:{unitId}</c>). A correction references
/// the corrected entry, applies the corrected value and leaves the original entry untouched; it never queues a notification, and
/// a reservation sent back by a correction keeps its unit, so reserving it again queues no second notification for that unit.
/// <para>
/// The domain transitions are T355 (<see cref="Replacement.Move"/>, <see cref="Replacement.RefuseUnit"/>,
/// <see cref="Replacement.Correct"/>); the <c>Moved</c>, <c>UnitRefused</c> and <c>Correction</c> requests of
/// <see cref="RecordReplacementEvent"/> T356.
/// </para>
/// </summary>
public sealed class ReplacementMoveCorrectionTests
{
    private const string PendingDomain = "Pending T355";
    private const string PendingUseCase = "Pending T356";

    private const string AgentActor = "agent-sub-t353";
    private const string ReviewerActor = "reviewer-sub-t353";
    private const string AuditorActor = "auditor-sub-t353";
    private const string MoveReason = "The customer moved to Hamburg and collects there.";
    private const string RefusalReason = "The customer refused the unit: the screen is scratched.";
    private const string CorrectionReason = "The serial was typed in wrongly; the location confirmed it.";
    private const string ClaimSerial = "AT10-24-5301";
    private const string WrongSerial = "AT10-24-5309";
    private const string FirstUnitSerial = "AT10-R-5301";
    private const string SecondUnitSerial = "AT10-R-5302";
    private const string ThirdUnitSerial = "AT10-R-5303";
    private const uint RowVersion = 3531;
    private const string ValidIfMatch = "\"3531\"";

    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid ClaimId = Guid.Parse("0199c000-0000-7000-8000-000000000353");
    private static readonly Guid VersionId = Guid.Parse("0199c000-0000-7000-8000-0000000003b5");
    private static readonly Guid CustomerId = Guid.Parse("0199c000-0000-7000-8000-0000000003d5");
    private static readonly Guid ClaimedProduct = Guid.Parse("0199c000-0000-7000-8000-0000000003e5");
    private static readonly Guid SuggestedLocation = Guid.Parse("33333333-3333-7333-8333-333333333351");
    private static readonly Guid OtherLocation = Guid.Parse("33333333-3333-7333-8333-333333333352");
    private static readonly Guid OtherTenantLocation = Guid.Parse("33333333-3333-7333-8333-333333333359");
    private static readonly DateOnly PurchaseDate = new(2026, 2, 2);
    private static readonly DateTimeOffset SubmittedAt = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly FirstDay = new(2026, 10, 2);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 14, 30, 0, TimeSpan.Zero);

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

    public ReplacementMoveCorrectionTests()
    {
        _tenant.IsResolved.Returns(true);
        _tenant.IsSystem.Returns(false);
        _tenant.TenantId.Returns(Tenant);
        _tenant.PrincipalId.Returns(AgentActor);
        _tenant.PrincipalName.Returns("Aurora Agent");
        _tenant.Roles.Returns(new HashSet<string>(StringComparer.Ordinal) { Principals.ClaimsAgentRole });

        _unitOfWork.ExecuteInTransactionAsync(default!, default)
            .ReturnsForAnyArgs(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        _catalog.GetProductAsync(ClaimedProduct, Arg.Any<CancellationToken>())
            .Returns(Product.Create(ClaimedProduct, Tenant, "AUR-TAB10", "Aurora Tab 10", "tablet", 480m, "EUR"));
        _network.ListEligibleAsync(Region.EU, "tablet", ServiceCenterPurpose.Replacement, Arg.Any<CancellationToken>()).Returns(
        [
            new EligibleServiceCenter(SuggestedLocation, "Aurora Service Center Amsterdam", Region.EU, "Voorbeeldstraat 12", "Mon–Fri", null, 10, true),
            new EligibleServiceCenter(OtherLocation, "Aurora Service Center Hamburg", Region.EU, "Beispielweg 7", "Mon–Fri", null, 20, true),
        ]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Move (FR-009, FR-010; X16) ─────────────────────────────────────────────────────────────

    [Fact(Skip = PendingDomain)]
    public void A_move_of_a_ready_replacement_leaves_one_active_location_releases_the_unit_and_awaits_a_new_unit()
    {
        var replacement = ReadyForCollection();
        var suggested = replacement.ActiveLocation.ShouldNotBeNull();
        var unit = replacement.CurrentUnit.ShouldNotBeNull();

        var recorded = replacement.Move(OtherLocation, MoveReason, Report(2, StaffRole.ClaimsAgent));

        replacement.Status.ShouldBe(ReplacementStatus.AwaitingUnit);
        replacement.Locations.Count(l => l.IsActive).ShouldBe(1);
        replacement.Locations.Count.ShouldBe(2, "the old location stays in the history");
        var active = replacement.ActiveLocation.ShouldNotBeNull();
        (active.ServiceCenterId, active.Assignment, active.Reason, active.RecordedBy).ShouldBe(
            (OtherLocation, LocationAssignment.Moved, MoveReason, AgentActor));
        suggested.IsActive.ShouldBeFalse();

        // The unit reserved at the old location stays in the history as released.
        replacement.Units.ShouldHaveSingleItem().ShouldBeSameAs(unit);
        (unit.Result, unit.ServiceCenterId, unit.SerialNumber).ShouldBe((ReplacementUnitResult.Released, SuggestedLocation, FirstUnitSerial));
        unit.EndedAt.ShouldNotBeNull();

        (recorded.Type, recorded.FromStatus, recorded.ToStatus, recorded.Actor).ShouldBe(
            (ReplacementEventType.Moved, (ReplacementStatus?)ReplacementStatus.ReadyForCollection,
                (ReplacementStatus?)ReplacementStatus.AwaitingUnit, AgentActor));
        var payload = Payload(recorded);
        payload.GetProperty("serviceCenterId").GetGuid().ShouldBe(OtherLocation);
        payload.GetProperty("previousServiceCenterId").GetGuid().ShouldBe(SuggestedLocation);
        payload.GetProperty("reason").GetString().ShouldBe(MoveReason);
        payload.GetProperty("releasedUnitId").GetGuid().ShouldBe(unit.Id);

        replacement.AllowedEvents(StaffRole.ClaimsAgent).ShouldContain(ReplacementEventType.UnitReserved);
        replacement.AllowedEvents(StaffRole.ClaimsAgent).ShouldNotContain(ReplacementEventType.HandedToCustomer);
    }

    [Fact(Skip = PendingDomain)]
    public void A_unit_reserved_after_a_move_is_a_new_unit_at_the_new_location()
    {
        var replacement = ReadyForCollection();
        var first = replacement.CurrentUnit.ShouldNotBeNull();
        replacement.Move(OtherLocation, MoveReason, Report(2, StaffRole.ClaimsAgent));

        replacement.ReserveUnit(ClaimedProduct, SecondUnitSerial, Report(3, StaffRole.ClaimsAgent));

        replacement.Status.ShouldBe(ReplacementStatus.ReadyForCollection);
        var second = replacement.CurrentUnit.ShouldNotBeNull();
        second.Id.ShouldNotBe(first.Id, "a new reservation is a new unit with its own ready notification (research R7)");
        RecordReplacementEvent.ReadyDedupeKey(second.Id).ShouldNotBe(RecordReplacementEvent.ReadyDedupeKey(first.Id));
        (second.Seq, second.ServiceCenterId, second.Result).ShouldBe((2, OtherLocation, ReplacementUnitResult.Reserved));
        first.Result.ShouldBe(ReplacementUnitResult.Released);
    }

    [Theory(Skip = PendingDomain)]
    [InlineData(ReplacementStatus.AwaitingReturn)]
    [InlineData(ReplacementStatus.AwaitingUnit)]
    public void A_move_before_a_reservation_keeps_the_status(ReplacementStatus status)
    {
        var replacement = status == ReplacementStatus.AwaitingReturn
            ? Create(ReplacementReturnRule.BeforeReservation, ReplacementStatus.AwaitingReturn)
            : AwaitingUnit();

        var recorded = replacement.Move(OtherLocation, MoveReason, Report(1, StaffRole.ClaimsReviewer));

        (replacement.Status, recorded.FromStatus, recorded.ToStatus).ShouldBe((status, (ReplacementStatus?)status, (ReplacementStatus?)status));
        replacement.ActiveLocation.ShouldNotBeNull().ServiceCenterId.ShouldBe(OtherLocation);
        replacement.Locations.Count(l => l.IsActive).ShouldBe(1);
        replacement.Units.ShouldBeEmpty();
        Payload(recorded).TryGetProperty("releasedUnitId", out var released).ShouldBeTrue();
        released.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory(Skip = PendingDomain)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_move_needs_a_reason(string? reason)
    {
        var replacement = ReadyForCollection();
        var events = replacement.Events.Count;

        Should.Throw<ArgumentException>(() => replacement.Move(OtherLocation, reason!, Report(2, StaffRole.ClaimsAgent)))
            .ParamName.ShouldBe("reason");

        ShouldBeReadyAtTheSuggestedLocation(replacement, events);
    }

    [Fact(Skip = PendingDomain)]
    public void A_move_needs_another_location_than_the_active_one()
    {
        var replacement = ReadyForCollection();
        var events = replacement.Events.Count;

        Should.Throw<ArgumentException>(() => replacement.Move(SuggestedLocation, MoveReason, Report(2, StaffRole.ClaimsAgent)))
            .ParamName.ShouldBe("serviceCenterId");

        ShouldBeReadyAtTheSuggestedLocation(replacement, events);
    }

    [Fact(Skip = PendingDomain)]
    public void An_auditor_cannot_move_refuse_or_correct()
    {
        var replacement = ReadyForCollection();
        var events = replacement.Events.Count;
        var reserved = replacement.Events[^1];

        Should.Throw<UnauthorizedAccessException>(() => replacement.Move(OtherLocation, MoveReason, Report(2, StaffRole.Auditor)));
        Should.Throw<UnauthorizedAccessException>(() => replacement.RefuseUnit(RefusalReason, Report(2, StaffRole.Auditor)));
        Should.Throw<UnauthorizedAccessException>(() => replacement.Correct(
            reserved.Seq, CorrectionReason, new ReplacementCorrection(SerialNumber: SecondUnitSerial), Report(2, StaffRole.Auditor)));

        ShouldBeReadyAtTheSuggestedLocation(replacement, events);
    }

    // ── Customer refused unit (FR-014, FR-027; X16) ───────────────────────────────────────────

    [Fact(Skip = PendingDomain)]
    public void A_refused_unit_stays_in_the_history_with_its_reason_and_the_replacement_awaits_a_new_unit()
    {
        var replacement = ReadyForCollection();
        var unit = replacement.CurrentUnit.ShouldNotBeNull();

        var recorded = replacement.RefuseUnit(RefusalReason, Report(2, StaffRole.ClaimsAgent));

        replacement.Status.ShouldBe(ReplacementStatus.AwaitingUnit);
        replacement.Units.ShouldHaveSingleItem().ShouldBeSameAs(unit);
        (unit.Result, unit.ResultReason, unit.SerialNumber).ShouldBe((ReplacementUnitResult.Refused, RefusalReason, FirstUnitSerial));
        unit.EndedAt.ShouldNotBeNull();
        replacement.ActiveLocation.ShouldNotBeNull().ServiceCenterId.ShouldBe(SuggestedLocation, "a refusal keeps the location");

        (recorded.Type, recorded.FromStatus, recorded.ToStatus).ShouldBe(
            (ReplacementEventType.UnitRefused, (ReplacementStatus?)ReplacementStatus.ReadyForCollection,
                (ReplacementStatus?)ReplacementStatus.AwaitingUnit));
        var payload = Payload(recorded);
        payload.GetProperty("unitId").GetGuid().ShouldBe(unit.Id);
        payload.GetProperty("reason").GetString().ShouldBe(RefusalReason);

        // A new reservation after the refusal is a new unit.
        replacement.ReserveUnit(ClaimedProduct, SecondUnitSerial, Report(3, StaffRole.ClaimsAgent));
        replacement.Units.Count.ShouldBe(2);
        replacement.CurrentUnit.ShouldNotBeNull().Id.ShouldNotBe(unit.Id);
        unit.Result.ShouldBe(ReplacementUnitResult.Refused);
    }

    [Theory(Skip = PendingDomain)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_refusal_needs_a_reason(string? reason)
    {
        var replacement = ReadyForCollection();
        var events = replacement.Events.Count;

        Should.Throw<ArgumentException>(() => replacement.RefuseUnit(reason!, Report(2, StaffRole.ClaimsAgent))).ParamName.ShouldBe("reason");

        ShouldBeReadyAtTheSuggestedLocation(replacement, events);
    }

    [Fact(Skip = PendingDomain)]
    public void A_unit_can_be_refused_only_while_ready_for_collection()
    {
        var replacement = AwaitingUnit();

        Should.Throw<InvalidOperationException>(() => replacement.RefuseUnit(RefusalReason, Report(1, StaffRole.ClaimsAgent)))
            .Message.ShouldContain(nameof(ReplacementStatus.AwaitingUnit));
        (replacement.Status, replacement.Events.Count).ShouldBe((ReplacementStatus.AwaitingUnit, 1));
    }

    // ── Corrections (FR-017; X17) ─────────────────────────────────────────────────────────────

    [Fact(Skip = PendingDomain)]
    public void A_serial_correction_applies_to_the_same_unit_and_keeps_the_original_entry()
    {
        var replacement = ReadyForCollection();
        var unit = replacement.CurrentUnit.ShouldNotBeNull();
        var reserved = replacement.Events[^1];
        var originalPayload = reserved.PayloadJson;

        var recorded = replacement.Correct(
            reserved.Seq, CorrectionReason, new ReplacementCorrection(SerialNumber: " at10-r-5309 "), Report(2, StaffRole.ClaimsAgent));

        unit.SerialNumber.ShouldBe("AT10-R-5309");
        replacement.CurrentUnit.ShouldBeSameAs(unit, "the corrected unit keeps its ID, so no second notification is due for it");
        replacement.Units.ShouldHaveSingleItem();
        replacement.Status.ShouldBe(ReplacementStatus.ReadyForCollection);

        // The original entry is untouched and still listed; the correction references it.
        replacement.Events.ShouldContain(reserved);
        (reserved.Type, reserved.PayloadJson).ShouldBe((ReplacementEventType.UnitReserved, originalPayload));
        (recorded.Type, recorded.Seq, recorded.CorrectsEventId).ShouldBe((ReplacementEventType.Correction, reserved.Seq + 1, (long?)reserved.Id));
        (recorded.FromStatus, recorded.ToStatus).ShouldBe(
            ((ReplacementStatus?)ReplacementStatus.ReadyForCollection, (ReplacementStatus?)ReplacementStatus.ReadyForCollection));
        var payload = Payload(recorded);
        payload.GetProperty("correctsSeq").GetInt32().ShouldBe(reserved.Seq);
        payload.GetProperty("reason").GetString().ShouldBe(CorrectionReason);
        payload.GetProperty("previousSerialNumber").GetString().ShouldBe(FirstUnitSerial);
        payload.GetProperty("serialNumber").GetString().ShouldBe("AT10-R-5309");
    }

    [Fact(Skip = PendingDomain)]
    public void A_return_date_correction_changes_the_return_and_keeps_its_serial_check()
    {
        var replacement = AwaitingUnit();
        var returned = replacement.RecordReturn(FirstDay, ClaimSerial, serialMatches: true, Report(1, StaffRole.ClaimsAgent));

        replacement.Correct(
            returned.Seq, CorrectionReason, new ReplacementCorrection(ReturnedOn: FirstDay.AddDays(-1)), Report(2, StaffRole.ClaimsAgent));

        var faultyReturn = replacement.Return.ShouldNotBeNull();
        (faultyReturn.ReturnedOn, faultyReturn.SerialSeen, faultyReturn.SerialMatches).ShouldBe((FirstDay.AddDays(-1), ClaimSerial, true));
        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingUnit, ReturnState.Returned));
        Payload(returned).GetProperty("returnedOn").GetString().ShouldBe("2026-10-02", "the original entry keeps the date it recorded");
    }

    [Fact(Skip = PendingDomain)]
    public void A_corrected_serial_seen_that_matches_resolves_the_mismatch_and_the_replacement_awaits_a_unit()
    {
        var replacement = Create(ReplacementReturnRule.BeforeReservation, ReplacementStatus.AwaitingReturn);
        var returned = replacement.RecordReturn(FirstDay, WrongSerial, serialMatches: false, Report(1, StaffRole.ClaimsAgent));
        replacement.ReturnState.ShouldBe(ReturnState.Mismatch);

        var recorded = replacement.Correct(
            returned.Seq, CorrectionReason, new ReplacementCorrection(SerialSeen: ClaimSerial) { SerialSeenMatches = true },
            Report(2, StaffRole.ClaimsAgent));

        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingUnit, ReturnState.Returned));
        var faultyReturn = replacement.Return.ShouldNotBeNull();
        (faultyReturn.SerialSeen, faultyReturn.SerialMatches).ShouldBe((ClaimSerial, true));
        (recorded.FromStatus, recorded.ToStatus).ShouldBe(
            ((ReplacementStatus?)ReplacementStatus.AwaitingReturn, (ReplacementStatus?)ReplacementStatus.AwaitingUnit));
        Payload(recorded).GetProperty("previousSerialSeen").GetString().ShouldBe(WrongSerial);
        Payload(returned).GetProperty("serialSeen").GetString().ShouldBe(WrongSerial);
    }

    [Fact(Skip = PendingDomain)]
    public void A_corrected_serial_seen_that_does_not_match_blocks_the_reservation_until_a_reviewer_accepts_it()
    {
        var replacement = AwaitingUnit();
        var returned = replacement.RecordReturn(FirstDay, ClaimSerial, serialMatches: true, Report(1, StaffRole.ClaimsAgent));

        replacement.Correct(
            returned.Seq, CorrectionReason, new ReplacementCorrection(SerialSeen: WrongSerial) { SerialSeenMatches = false },
            Report(2, StaffRole.ClaimsAgent));

        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingUnit, ReturnState.Mismatch));
        replacement.AllowedEvents(StaffRole.ClaimsAgent).ShouldNotContain(ReplacementEventType.UnitReserved);
        replacement.AllowedEvents(StaffRole.ClaimsReviewer).ShouldContain(ReplacementEventType.ReturnAccepted);
    }

    [Fact(Skip = PendingDomain)]
    public void A_reservation_sent_back_by_a_correction_keeps_its_unit_and_reserving_again_reuses_it()
    {
        var replacement = ReadyForCollection();
        var unit = replacement.CurrentUnit.ShouldNotBeNull();
        var reserved = replacement.Events[^1];
        var firstKey = RecordReplacementEvent.ReadyDedupeKey(unit.Id);

        var recorded = replacement.Correct(
            reserved.Seq, "The location had not reserved the unit yet; recorded too early.",
            new ReplacementCorrection(ToStatus: ReplacementStatus.AwaitingUnit), Report(2, StaffRole.ClaimsAgent));

        (replacement.Status, recorded.FromStatus, recorded.ToStatus).ShouldBe(
            (ReplacementStatus.AwaitingUnit, (ReplacementStatus?)ReplacementStatus.ReadyForCollection, (ReplacementStatus?)ReplacementStatus.AwaitingUnit));
        replacement.AllowedEvents(StaffRole.ClaimsAgent).ShouldContain(ReplacementEventType.UnitReserved);
        replacement.AllowedEvents(StaffRole.ClaimsAgent).ShouldNotContain(ReplacementEventType.HandedToCustomer);

        // Reserving again re-confirms the same unit: same ID, same dedupe key, so no second ready notification (edge cases).
        replacement.ReserveUnit(ClaimedProduct, SecondUnitSerial, Report(3, StaffRole.ClaimsAgent));

        replacement.Status.ShouldBe(ReplacementStatus.ReadyForCollection);
        replacement.Units.ShouldHaveSingleItem().ShouldBeSameAs(unit);
        (unit.Result, unit.SerialNumber).ShouldBe((ReplacementUnitResult.Reserved, SecondUnitSerial));
        RecordReplacementEvent.ReadyDedupeKey(replacement.CurrentUnitId!.Value).ShouldBe(firstKey);
        replacement.Events.Count(e => e.Type == ReplacementEventType.UnitReserved).ShouldBe(2);
        reserved.PayloadJson.ShouldContain(FirstUnitSerial);
    }

    [Fact(Skip = PendingDomain)]
    public void A_refusal_sent_back_by_a_correction_reinstates_the_refused_unit()
    {
        var replacement = ReadyForCollection();
        var unit = replacement.CurrentUnit.ShouldNotBeNull();
        var refused = replacement.RefuseUnit(RefusalReason, Report(2, StaffRole.ClaimsAgent));

        replacement.Correct(
            refused.Seq, "The refusal was recorded on the wrong claim.", new ReplacementCorrection(ToStatus: ReplacementStatus.ReadyForCollection),
            Report(3, StaffRole.ClaimsAgent));

        replacement.Status.ShouldBe(ReplacementStatus.ReadyForCollection);
        replacement.CurrentUnit.ShouldBeSameAs(unit);
        (unit.Result, unit.ResultReason, unit.EndedAt).ShouldBe((ReplacementUnitResult.Reserved, (string?)null, (DateTimeOffset?)null));
        replacement.Units.ShouldHaveSingleItem();
        refused.Type.ShouldBe(ReplacementEventType.UnitRefused, "the refusal stays in the history");
    }

    [Fact(Skip = PendingDomain)]
    public void Only_the_latest_reservation_or_refusal_can_be_sent_back_and_only_to_its_previous_status()
    {
        var replacement = ReadyForCollection();
        var reserved = replacement.Events[^1];
        var moved = replacement.Move(OtherLocation, MoveReason, Report(2, StaffRole.ClaimsAgent));
        var events = replacement.Events.Count;

        // The reservation is no longer the latest status change, and a move cannot be sent back.
        Should.Throw<ArgumentException>(() => replacement.Correct(
                reserved.Seq, CorrectionReason, new ReplacementCorrection(ToStatus: ReplacementStatus.AwaitingUnit), Report(3, StaffRole.ClaimsAgent)))
            .ParamName.ShouldBe("toStatus");
        Should.Throw<ArgumentException>(() => replacement.Correct(
                moved.Seq, CorrectionReason, new ReplacementCorrection(ToStatus: ReplacementStatus.ReadyForCollection), Report(3, StaffRole.ClaimsAgent)))
            .ParamName.ShouldBe("toStatus");

        var reservedAgain = replacement.ReserveUnit(ClaimedProduct, SecondUnitSerial, Report(3, StaffRole.ClaimsAgent));
        Should.Throw<ArgumentException>(() => replacement.Correct(
                reservedAgain.Seq, CorrectionReason, new ReplacementCorrection(ToStatus: ReplacementStatus.ReadyForCollection),
                Report(4, StaffRole.ClaimsAgent)))
            .ParamName.ShouldBe("toStatus");

        replacement.Status.ShouldBe(ReplacementStatus.ReadyForCollection);
        replacement.Events.Count.ShouldBe(events + 1);
    }

    [Fact(Skip = PendingDomain)]
    public void A_not_available_entry_cannot_be_sent_back()
    {
        var replacement = AwaitingUnit();
        var notAvailable = replacement.MarkNotAvailable("No unit of the model is left at any location.", Report(1, StaffRole.ClaimsAgent));

        replacement.AllowedEvents(StaffRole.ClaimsAgent).ShouldBe([ReplacementEventType.Correction]);
        Should.Throw<ArgumentException>(() => replacement.Correct(
                notAvailable.Seq, CorrectionReason, new ReplacementCorrection(ToStatus: ReplacementStatus.AwaitingUnit), Report(2, StaffRole.ClaimsAgent)))
            .ParamName.ShouldBe("toStatus");
        replacement.Status.ShouldBe(ReplacementStatus.NotAvailable);
    }

    [Theory(Skip = PendingDomain)]
    [InlineData("noReason", "reason")]
    [InlineData("unknownEntry", "correctsSeq")]
    [InlineData("systemEntry", "correctsSeq")]
    [InlineData("correctionEntry", "correctsSeq")]
    [InlineData("noValue", "correction")]
    [InlineData("twoValues", "correction")]
    [InlineData("serialOfAReturn", "serialNumber")]
    [InlineData("returnDateOfAReservation", "returnedOn")]
    [InlineData("serialSeenOfAReservation", "serialSeen")]
    [InlineData("handOverDate", "handedOverOn")]
    public void A_correction_needs_a_reason_an_entry_to_correct_and_one_value_that_fits_it(string request, string param)
    {
        var replacement = ReadyForCollection();
        var reserved = replacement.Events[^1];
        var returned = replacement.RecordReturn(FirstDay.AddDays(1), ClaimSerial, serialMatches: true, Report(1, StaffRole.ClaimsAgent));
        var correction = replacement.Correct(
            reserved.Seq, CorrectionReason, new ReplacementCorrection(SerialNumber: SecondUnitSerial), Report(2, StaffRole.ClaimsAgent));
        var suggested = replacement.Events.Single(e => e.Type == ReplacementEventType.LocationSuggested);
        var events = replacement.Events.Count;
        var serial = new ReplacementCorrection(SerialNumber: ThirdUnitSerial);

        Action correct = request switch
        {
            "noReason" => () => replacement.Correct(reserved.Seq, " ", serial, Report(3, StaffRole.ClaimsAgent)),
            "unknownEntry" => () => replacement.Correct(99, CorrectionReason, serial, Report(3, StaffRole.ClaimsAgent)),
            "systemEntry" => () => replacement.Correct(suggested.Seq, CorrectionReason, serial, Report(3, StaffRole.ClaimsAgent)),
            "correctionEntry" => () => replacement.Correct(correction.Seq, CorrectionReason, serial, Report(3, StaffRole.ClaimsAgent)),
            "noValue" => () => replacement.Correct(reserved.Seq, CorrectionReason, new ReplacementCorrection(), Report(3, StaffRole.ClaimsAgent)),
            "twoValues" => () => replacement.Correct(
                reserved.Seq, CorrectionReason, new ReplacementCorrection(SerialNumber: ThirdUnitSerial, ReturnedOn: FirstDay),
                Report(3, StaffRole.ClaimsAgent)),
            "serialOfAReturn" => () => replacement.Correct(returned.Seq, CorrectionReason, serial, Report(3, StaffRole.ClaimsAgent)),
            "returnDateOfAReservation" => () => replacement.Correct(
                reserved.Seq, CorrectionReason, new ReplacementCorrection(ReturnedOn: FirstDay), Report(3, StaffRole.ClaimsAgent)),
            "serialSeenOfAReservation" => () => replacement.Correct(
                reserved.Seq, CorrectionReason, new ReplacementCorrection(SerialSeen: ClaimSerial) { SerialSeenMatches = true },
                Report(3, StaffRole.ClaimsAgent)),
            "handOverDate" => () => replacement.Correct(
                reserved.Seq, CorrectionReason, new ReplacementCorrection(HandedOverOn: FirstDay), Report(3, StaffRole.ClaimsAgent)),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request, null),
        };

        Should.Throw<ArgumentException>(correct).ParamName.ShouldBe(param);
        replacement.Events.Count.ShouldBe(events);
        replacement.Status.ShouldBe(ReplacementStatus.ReadyForCollection);
        replacement.CurrentUnit.ShouldNotBeNull().SerialNumber.ShouldBe(SecondUnitSerial);
        replacement.Return.ShouldNotBeNull().ReturnedOn.ShouldBe(FirstDay.AddDays(1));
    }

    // ── Through the request (T356): notifications per unit, remedy unchanged, tenant isolation ──

    [Fact(Skip = PendingUseCase)]
    public async Task X16_move_reserve_refuse_reserve_queues_one_notification_per_reserved_unit_and_leaves_the_remedy_unchanged()
    {
        var (claim, replacement, remedy) = ArrangeAwaitingUnit();
        var keys = CaptureNotificationKeys();

        (await Execute(claim, Reserve(FirstUnitSerial, day: 1))).ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        var first = replacement.CurrentUnit.ShouldNotBeNull();
        (await Execute(claim, Move(OtherLocation, day: 2))).ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        (await Execute(claim, Reserve(SecondUnitSerial, day: 3))).ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        var second = replacement.CurrentUnit.ShouldNotBeNull();
        (await Execute(claim, Refuse(day: 4))).ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        (await Execute(claim, Reserve(ThirdUnitSerial, day: 5))).ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        var third = replacement.CurrentUnit.ShouldNotBeNull();

        replacement.Status.ShouldBe(ReplacementStatus.ReadyForCollection);
        replacement.Locations.Count(l => l.IsActive).ShouldBe(1);
        replacement.ActiveLocation.ShouldNotBeNull().ServiceCenterId.ShouldBe(OtherLocation);
        replacement.Units.Select(u => (u.Seq, u.ServiceCenterId, u.Result)).ShouldBe(
        [
            (1, SuggestedLocation, ReplacementUnitResult.Released),
            (2, OtherLocation, ReplacementUnitResult.Refused),
            (3, OtherLocation, ReplacementUnitResult.Reserved),
        ]);
        keys.ShouldBe([first.Id, second.Id, third.Id], ignoreOrder: false);
        keys.Distinct().Count().ShouldBe(3);

        // FR-027: a move or a refused unit never changes the remedy by itself.
        (remedy.Remedy, remedy.State).ShouldBe(((RemedyType?)RemedyType.Replacement, RemedyState.InProgress));
        _remedies.DidNotReceiveWithAnyArgs().AddDecision(default!);
        await _trail.Received(1).AppendAsync(
            claim.Id, TrailStep.ReplacementLocationAssigned, AgentActor, Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact(Skip = PendingUseCase)]
    public async Task A_move_to_a_location_of_another_company_is_404_with_a_security_event_and_changes_nothing()
    {
        var (claim, replacement, _) = ArrangeAwaitingUnit();

        var result = await Execute(claim, Move(OtherTenantLocation, day: 1));

        result.ShouldBeOfType<RecordReplacementEventResult.NotFound>();
        await _securityEvents.Received(1).RecordAsync(
            SecurityEventKind.AccessDenied, Arg.Any<string>(), OtherTenantLocation.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
        replacement.ActiveLocation.ShouldNotBeNull().ServiceCenterId.ShouldBe(SuggestedLocation);
        replacement.Events.Count.ShouldBe(1);
        _replacements.DidNotReceiveWithAnyArgs().AddEvent(default!);
    }

    [Theory(Skip = PendingUseCase)]
    [InlineData("Moved", "locationId")]
    [InlineData("Moved", "reason")]
    [InlineData("UnitRefused", "reason")]
    [InlineData("Correction", "correctsSeq")]
    [InlineData("Correction", "reason")]
    [InlineData("Correction", "correction")]
    public async Task Moves_refusals_and_corrections_name_a_missing_field(string type, string field)
    {
        var (claim, replacement, _) = ArrangeAwaitingUnit();
        replacement.ReserveUnit(ClaimedProduct, FirstUnitSerial, Report(1, StaffRole.ClaimsAgent));
        var reserved = replacement.Events[^1];
        var command = type switch
        {
            "Moved" => Move(OtherLocation, day: 2),
            "UnitRefused" => Refuse(day: 2),
            _ => Correct(reserved.Seq, day: 2) with { SerialNumber = SecondUnitSerial },
        };
        command = field switch
        {
            "locationId" => command with { LocationId = null },
            "reason" => command with { Reason = "short" },
            "correctsSeq" => command with { CorrectsSeq = null },
            _ => command with { SerialNumber = null },
        };

        var result = await Execute(claim, command);

        result.ShouldBeOfType<RecordReplacementEventResult.Invalid>().Errors.Keys.ShouldContain(field);
        replacement.Status.ShouldBe(ReplacementStatus.ReadyForCollection);
        _replacements.DidNotReceiveWithAnyArgs().AddEvent(default!);
    }

    [Fact(Skip = PendingUseCase)]
    public async Task X17_a_serial_correction_applies_to_the_unit_keeps_the_original_entry_and_queues_no_notification()
    {
        var (claim, replacement, _) = ArrangeAwaitingUnit();
        (await Execute(claim, Reserve(FirstUnitSerial, day: 1))).ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        var unit = replacement.CurrentUnit.ShouldNotBeNull();
        var reserved = replacement.Events[^1];
        _notifications.ClearReceivedCalls();
        _trail.ClearReceivedCalls();

        var result = await Execute(claim, Correct(reserved.Seq, day: 2) with { SerialNumber = SecondUnitSerial });

        result.ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        unit.SerialNumber.ShouldBe(SecondUnitSerial);
        replacement.CurrentUnit.ShouldBeSameAs(unit);
        var correction = replacement.Events[^1];
        (correction.Type, correction.CorrectsEventId).ShouldBe((ReplacementEventType.Correction, (long?)reserved.Id));
        reserved.PayloadJson.ShouldContain(FirstUnitSerial);
        _notifications.ReceivedCalls().ShouldBeEmpty();
        await _trail.Received(1).AppendAsync(
            claim.Id, TrailStep.ReplacementStatusUpdated, AgentActor, Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Theory(Skip = PendingUseCase)]
    [InlineData(ClaimSerial)]
    [InlineData(" at10-24-5301 ")]
    public async Task A_corrected_unit_serial_follows_the_reservation_rules(string serial)
    {
        var (claim, replacement, _) = ArrangeAwaitingUnit();
        replacement.ReserveUnit(ClaimedProduct, FirstUnitSerial, Report(1, StaffRole.ClaimsAgent));
        var reserved = replacement.Events[^1];

        var result = await Execute(claim, Correct(reserved.Seq, day: 2) with { SerialNumber = serial });

        result.ShouldBeOfType<RecordReplacementEventResult.Invalid>().Errors.Keys.ShouldContain("serialNumber");
        replacement.CurrentUnit.ShouldNotBeNull().SerialNumber.ShouldBe(FirstUnitSerial);
        _replacements.DidNotReceiveWithAnyArgs().AddEvent(default!);
    }

    [Fact(Skip = PendingUseCase)]
    public async Task A_reservation_sent_back_and_reserved_again_queues_no_second_notification_for_the_unit()
    {
        var (claim, replacement, _) = ArrangeAwaitingUnit();
        var keys = CaptureNotificationKeys();
        (await Execute(claim, Reserve(FirstUnitSerial, day: 1))).ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        var reserved = replacement.Events[^1];

        (await Execute(claim, Correct(reserved.Seq, day: 2) with { ToStatus = ReplacementStatus.AwaitingUnit }))
            .ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        replacement.Status.ShouldBe(ReplacementStatus.AwaitingUnit);
        (await Execute(claim, Reserve(FirstUnitSerial, day: 3))).ShouldBeOfType<RecordReplacementEventResult.Recorded>();

        replacement.Status.ShouldBe(ReplacementStatus.ReadyForCollection);
        replacement.Units.ShouldHaveSingleItem();
        keys.ShouldHaveSingleItem().ShouldBe(replacement.CurrentUnitId!.Value);
    }

    [Fact(Skip = PendingUseCase)]
    public async Task A_correction_with_a_return_date_in_the_future_is_refused()
    {
        var (claim, replacement, _) = ArrangeAwaitingUnit();
        var returned = replacement.RecordReturn(FirstDay, ClaimSerial, serialMatches: true, Report(1, StaffRole.ClaimsAgent));

        var result = await Execute(
            claim, Correct(returned.Seq, day: 2) with { ReturnedOn = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(1) });

        result.ShouldBeOfType<RecordReplacementEventResult.Invalid>().Errors.Keys.ShouldContain("returnedOn");
        replacement.Return.ShouldNotBeNull().ReturnedOn.ShouldBe(FirstDay);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static string ActorOf(StaffRole role) => role switch
    {
        StaffRole.ClaimsAgent => AgentActor,
        StaffRole.ClaimsReviewer => ReviewerActor,
        _ => AuditorActor,
    };

    /// <summary>A report by phone with <paramref name="role"/>, <paramref name="day"/> days after the first day.</summary>
    private static ReplacementReport Report(int day, StaffRole role)
    {
        var at = new DateTimeOffset(FirstDay.AddDays(day).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        return new ReplacementReport(at, ReportSource.Phone, ActorOf(role), role, null, at.AddMinutes(5));
    }

    private static Replacement Create(ReplacementReturnRule returnRule, ReplacementStatus initialStatus, Guid? claimId = null)
        => Replacement.Create(
            Guid.CreateVersion7(), Tenant, claimId ?? ClaimId, VersionId, ReplacementProductRule.SameCategoryAtLeastClaimValue, returnRule,
            minimumCoverMonths: 0, initialStatus, SuggestedLocation, CreatedAt);

    /// <summary>Aurora's rule (X1): returned at collection, awaiting a unit at the suggested location.</summary>
    private static Replacement AwaitingUnit() => Create(ReplacementReturnRule.AtCollection, ReplacementStatus.AwaitingUnit);

    /// <summary>As <see cref="AwaitingUnit"/>, with <see cref="FirstUnitSerial"/> reserved at the suggested location on day 1.</summary>
    private static Replacement ReadyForCollection()
    {
        var replacement = AwaitingUnit();
        replacement.ReserveUnit(ClaimedProduct, FirstUnitSerial, Report(1, StaffRole.ClaimsAgent));
        return replacement;
    }

    /// <summary>A refused request: still ready for collection with the first unit at the suggested location, nothing appended.</summary>
    private static void ShouldBeReadyAtTheSuggestedLocation(Replacement replacement, int events)
    {
        replacement.Status.ShouldBe(ReplacementStatus.ReadyForCollection);
        replacement.Events.Count.ShouldBe(events);
        replacement.ActiveLocation.ShouldNotBeNull().ServiceCenterId.ShouldBe(SuggestedLocation);
        replacement.Locations.ShouldHaveSingleItem();
        var unit = replacement.CurrentUnit.ShouldNotBeNull();
        (unit.Result, unit.SerialNumber).ShouldBe((ReplacementUnitResult.Reserved, FirstUnitSerial));
    }

    private static JsonElement Payload(ReplacementEvent replacementEvent)
        => JsonDocument.Parse(replacementEvent.PayloadJson).RootElement.Clone();

    /// <summary>
    /// An approved Aurora claim (AUR-TAB10, Europe, serial <see cref="ClaimSerial"/>) whose remedy Replacement is in progress and
    /// whose replacement awaits a unit at the suggested location, behind the repositories.
    /// </summary>
    private (Claim Claim, Replacement Replacement, ClaimRemedy Remedy) ArrangeAwaitingUnit()
    {
        var claim = Claim.Submit(
            Guid.CreateVersion7(), Tenant, ClaimReference.Generate(), ClaimChannel.AgentPortal, "agent-sub-submitter", CustomerId,
            "claimant@synthetic-mail.test", null, "AUR-TAB10", ClaimedProduct, ClaimSerial, PurchaseDate, "Online shop", 480m,
            Region.EU, "The tablet no longer charges and the battery swells.", SubmittedAt);
        claim.StartEvaluation(SubmittedAt);
        claim.EscalateToReview(SubmittedAt);
        claim.FinalizeApproved("Your tablet is covered for manufacturing defects.", DecidedBy.Reviewer, SubmittedAt.AddDays(1));

        var remedy = ClaimRemedy.AwaitDecision(
            Guid.CreateVersion7(), Tenant, claim.Id, VersionId, RemedyScenario.RepairFailed,
            [RemedyType.Replacement], RemedyPendingReason.RepairFailedLimit, CreatedAt);
        remedy.Decide(RemedyType.Replacement, CreatedAt);
        var replacement = Create(ReplacementReturnRule.AtCollection, ReplacementStatus.AwaitingUnit, claim.Id);

        _claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);
        _remedies.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(remedy);
        _replacements.GetByClaimAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(replacement);
        _replacements.GetRowVersionAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(RowVersion);
        return (claim, replacement, remedy);
    }

    /// <summary>The unit IDs of the ready notifications queued, in order (from their <c>replacement-ready:{unitId}</c> keys).</summary>
    private List<Guid> CaptureNotificationKeys()
    {
        var keys = new List<Guid>();
        _notifications.EnqueueAsync(default, default!, default!, default).ReturnsForAnyArgs(call =>
        {
            call.ArgAt<string>(1).ShouldBe(RecordReplacementEvent.ReadyTemplate);
            var key = call.ArgAt<string>(2);
            key.ShouldStartWith("replacement-ready:");
            keys.Add(Guid.Parse(key["replacement-ready:".Length..]));
            return NotificationEnqueueResult.Queued(Guid.CreateVersion7());
        });
        return keys;
    }

    private Task<RecordReplacementEventResult> Execute(Claim claim, RecordReplacementEventCommand command)
        => new RecordReplacementEvent(
                _tenant, _claims, _catalog, _remedies, _replacements, _serials, _policies, _network, new RemedyPolicyEvaluator(),
                new ReplacementPolicy(), _notifications, _trail, _securityEvents, _unitOfWork, _time)
            .ExecuteAsync(command with { ClaimId = claim.Id }, Ct);

    /// <summary>10:00 UTC, <paramref name="day"/> days after <see cref="FirstDay"/> (after the set-up and before <see cref="Now"/>).</summary>
    private static DateTimeOffset ReportedOn(int day) => new(FirstDay.AddDays(day).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);

    private static RecordReplacementEventCommand Reserve(string serial, int day)
        => new(Guid.Empty, ValidIfMatch, ReplacementEventType.UnitReserved, ReportedOn(day), ReportSource.Phone) { SerialNumber = serial };

    private static RecordReplacementEventCommand Move(Guid locationId, int day)
        => new(Guid.Empty, ValidIfMatch, ReplacementEventType.Moved, ReportedOn(day), ReportSource.Phone)
        {
            LocationId = locationId,
            Reason = MoveReason,
        };

    private static RecordReplacementEventCommand Refuse(int day)
        => new(Guid.Empty, ValidIfMatch, ReplacementEventType.UnitRefused, ReportedOn(day), ReportSource.InPerson) { Reason = RefusalReason };

    private static RecordReplacementEventCommand Correct(int correctsSeq, int day)
        => new(Guid.Empty, ValidIfMatch, ReplacementEventType.Correction, ReportedOn(day), ReportSource.Email)
        {
            CorrectsSeq = correctsSeq,
            Reason = CorrectionReason,
        };
}
