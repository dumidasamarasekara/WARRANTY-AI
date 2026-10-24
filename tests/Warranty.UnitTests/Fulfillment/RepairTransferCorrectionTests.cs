using System.Text.Json;
using Warranty.Application.Fulfillment;
using Warranty.Domain.Fulfillment;

namespace Warranty.UnitTests.Fulfillment;

/// <summary>
/// Transfers and corrections of a repair (002 T254; FR-026, FR-027, FR-038, data-model.md "Repair"): a transfer leaves
/// exactly one active hand-over (the new centre, the old one kept in the history) and never changes the attempt count;
/// a correction references the corrected entry, applies the corrected value and leaves the original entry untouched; a
/// corrected ready mark reopens the same attempt, so marking it ready again cannot queue a second ready notification
/// (the notification is keyed by the attempt, research R7). Implemented in T256.
/// </summary>
public sealed class RepairTransferCorrectionTests
{
    private const string Actor = "agent-sub-t254";
    private const string TransferReason = "The first centre cannot get the replacement panel for weeks.";
    private const string CorrectionReason = "The hand-over date was typed in wrongly; the centre confirmed it.";
    private const string Failure = "The display flickers again after the panel was replaced.";

    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid ClaimId = Guid.Parse("0199b000-0000-7000-8000-000000000254");
    private static readonly Guid FirstCentre = Guid.Parse("11111111-0003-7000-8000-000000000001");
    private static readonly Guid SecondCentre = Guid.Parse("11111111-0003-7000-8000-000000000003");
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly HandedOverOn = new(2026, 9, 3);

    // ── Transfer (FR-026) ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_transfer_after_a_failure_leaves_one_active_hand_over_at_the_new_centre_and_the_attempt_count_unchanged()
    {
        var repair = AtCentreAfterOneFailure();
        var previous = repair.ActiveHandover.ShouldNotBeNull();
        var attempt = repair.CurrentAttempt.ShouldNotBeNull();
        var (attempts, handovers) = (repair.Attempts.Count, repair.Handovers.Count);

        var recorded = repair.Transfer(SecondCentre, TransferReason, Report(5));

        repair.Status.ShouldBe(RepairStatus.AwaitingHandover);
        repair.Handovers.Count(h => h.IsActive).ShouldBe(1);
        repair.Handovers.Count.ShouldBe(handovers + 1);
        var active = repair.ActiveHandover.ShouldNotBeNull();
        (active.ServiceCenterId, active.Assignment, active.Reason, active.RecordedBy).ShouldBe(
            (SecondCentre, HandoverAssignment.Transfer, TransferReason, Actor));
        previous.IsActive.ShouldBeFalse();
        previous.HandedOverOn.ShouldBe(HandedOverOn, "the old hand-over stays in the history");

        // The open attempt moves to the new centre; no attempt is added or ended and the failure count stays.
        (repair.Attempts.Count, repair.FailedAttempts).ShouldBe((attempts, 1));
        repair.CurrentAttempt.ShouldBeSameAs(attempt);
        (attempt.ServiceCenterId, attempt.Result).ShouldBe((SecondCentre, AttemptResult.Open));

        (recorded.Type, recorded.FromStatus, recorded.ToStatus).ShouldBe(
            (RepairEventType.Transferred, (RepairStatus?)RepairStatus.AtRepairCentre, (RepairStatus?)RepairStatus.AwaitingHandover));
        var payload = Payload(recorded);
        payload.GetProperty("serviceCenterId").GetGuid().ShouldBe(SecondCentre);
        payload.GetProperty("previousServiceCenterId").GetGuid().ShouldBe(FirstCentre);
    }

    [Fact]
    public void The_hand_over_at_the_new_centre_continues_the_same_attempt()
    {
        var repair = AtCentreAfterOneFailure();
        var attempt = repair.CurrentAttempt.ShouldNotBeNull();
        repair.Transfer(SecondCentre, TransferReason, Report(5));

        repair.RecordHandover(HandedOverOn.AddDays(6), "RIV-77", Report(6));

        repair.Status.ShouldBe(RepairStatus.AtRepairCentre);
        repair.CurrentAttempt.ShouldBeSameAs(attempt);
        repair.Attempts.Count.ShouldBe(2);
        repair.ActiveHandover.ShouldNotBeNull().ServiceCenterId.ShouldBe(SecondCentre);
    }

    [Fact]
    public void A_transfer_before_the_hand_over_only_changes_the_active_centre()
    {
        var repair = Repair.Create(Guid.CreateVersion7(), Tenant, ClaimId, FirstCentre, CreatedAt);

        repair.Transfer(SecondCentre, TransferReason, Report(0));

        (repair.Status, repair.Attempts.Count, repair.Handovers.Count(h => h.IsActive)).ShouldBe((RepairStatus.AwaitingHandover, 0, 1));
        repair.ActiveHandover.ShouldNotBeNull().ServiceCenterId.ShouldBe(SecondCentre);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_transfer_needs_a_reason(string? reason)
    {
        var repair = AtCentreAfterOneFailure();
        var events = repair.Events.Count;

        Should.Throw<ArgumentException>(() => repair.Transfer(SecondCentre, reason!, Report(5)));

        (repair.Status, repair.Events.Count).ShouldBe((RepairStatus.AtRepairCentre, events));
        repair.ActiveHandover.ShouldNotBeNull().ServiceCenterId.ShouldBe(FirstCentre);
    }

    [Fact]
    public void A_transfer_needs_another_centre()
    {
        var repair = AtCentreAfterOneFailure();

        Should.Throw<ArgumentException>(() => repair.Transfer(FirstCentre, TransferReason, Report(5)));

        repair.Handovers.Count(h => h.IsActive).ShouldBe(1);
        repair.Status.ShouldBe(RepairStatus.AtRepairCentre);
    }

    [Fact]
    public void A_ready_or_not_possible_repair_cannot_be_transferred()
    {
        var ready = AtCentre();
        ready.MarkReady(Report(2));
        var failed = AtCentre();
        failed.RecordFailure(Failure, Report(2));

        Should.Throw<InvalidOperationException>(() => ready.Transfer(SecondCentre, TransferReason, Report(3))).Message
            .ShouldContain(nameof(RepairStatus.ReadyForCollection));
        Should.Throw<InvalidOperationException>(() => failed.Transfer(SecondCentre, TransferReason, Report(3))).Message
            .ShouldContain(nameof(RepairStatus.RepairNotPossible));
    }

    // ── Correction (FR-027) ────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_hand_over_date_correction_applies_the_corrected_date_and_keeps_the_original_entry()
    {
        var repair = AtCentre();
        var original = repair.Events.Single(e => e.Type == RepairEventType.HandedOver);
        var originalPayload = original.PayloadJson;
        var corrected = HandedOverOn.AddDays(-1);

        var recorded = repair.Correct(original.Seq, CorrectionReason, new RepairCorrection(HandedOverOn: corrected), Report(1));

        repair.ActiveHandover.ShouldNotBeNull().HandedOverOn.ShouldBe(corrected);
        (recorded.Type, recorded.CorrectsEventId, recorded.FromStatus, recorded.ToStatus).ShouldBe(
            (RepairEventType.Correction, (long?)original.Id, (RepairStatus?)RepairStatus.AtRepairCentre, (RepairStatus?)RepairStatus.AtRepairCentre));
        var payload = Payload(recorded);
        payload.GetProperty("correctsSeq").GetInt32().ShouldBe(original.Seq);
        payload.GetProperty("reason").GetString().ShouldBe(CorrectionReason);

        // The original entry is untouched and still in the history.
        repair.Events.ShouldContain(original);
        (original.Type, original.PayloadJson).ShouldBe((RepairEventType.HandedOver, originalPayload));
        repair.Events[^1].ShouldBeSameAs(recorded);
        repair.Status.ShouldBe(RepairStatus.AtRepairCentre);
    }

    [Fact]
    public void A_correction_of_an_old_hand_over_after_a_transfer_corrects_that_hand_over()
    {
        var repair = AtCentreAfterOneFailure();
        var first = repair.ActiveHandover.ShouldNotBeNull();
        var handedOver = repair.Events.Single(e => e.Type == RepairEventType.HandedOver);
        repair.Transfer(SecondCentre, TransferReason, Report(5));

        repair.Correct(handedOver.Seq, CorrectionReason, new RepairCorrection(HandedOverOn: HandedOverOn.AddDays(-1)), Report(5));

        first.HandedOverOn.ShouldBe(HandedOverOn.AddDays(-1));
        repair.ActiveHandover.ShouldNotBeNull().ServiceCenterId.ShouldBe(SecondCentre);
        (repair.Status, repair.FailedAttempts, repair.Handovers.Count(h => h.IsActive)).ShouldBe((RepairStatus.AwaitingHandover, 1, 1));
    }

    [Fact]
    public void Centre_reference_and_estimate_corrections_apply_the_corrected_values()
    {
        var repair = AtCentre();
        var handedOver = repair.Events.Single(e => e.Type == RepairEventType.HandedOver);
        var estimate = repair.RecordEstimate(120m, 288m, Report(1));

        repair.Correct(handedOver.Seq, CorrectionReason, new RepairCorrection(CentreReference: "JOB-1010"), Report(2));
        repair.Correct(estimate.Seq, CorrectionReason, new RepairCorrection(EstimateAmount: 210m), Report(2));

        repair.ActiveHandover.ShouldNotBeNull().CentreReference.ShouldBe("JOB-1010");
        repair.CurrentAttempt.ShouldNotBeNull().EstimateAmount.ShouldBe(210m);
        Payload(estimate).GetProperty("estimateAmount").GetDecimal().ShouldBe(120m);
        repair.Events.Count(e => e.Type == RepairEventType.Correction).ShouldBe(2);
    }

    [Fact]
    public void A_corrected_ready_mark_reopens_the_same_attempt_so_a_second_ready_mark_has_the_same_notification_key()
    {
        var repair = AtCentre();
        var marked = repair.MarkReady(Report(2));
        var attempt = repair.CurrentAttempt.ShouldNotBeNull();
        var firstKey = RecordRepairEvent.ReadyDedupeKey(attempt.Id);

        var recorded = repair.Correct(marked.Seq, CorrectionReason, new RepairCorrection(ToStatus: RepairStatus.AtRepairCentre), Report(2));

        (recorded.FromStatus, recorded.ToStatus).ShouldBe(((RepairStatus?)RepairStatus.ReadyForCollection, (RepairStatus?)RepairStatus.AtRepairCentre));
        repair.Status.ShouldBe(RepairStatus.AtRepairCentre);
        (attempt.Result, attempt.EndedAt).ShouldBe((AttemptResult.Open, (DateTimeOffset?)null));

        repair.MarkReady(Report(3));

        repair.CurrentAttempt.ShouldBeSameAs(attempt);
        repair.Attempts.ShouldHaveSingleItem();
        RecordRepairEvent.ReadyDedupeKey(repair.CurrentAttemptId!.Value).ShouldBe(firstKey);
        repair.Events.Count(e => e.Type == RepairEventType.MarkedReady).ShouldBe(2);
    }

    [Fact]
    public void A_corrected_failure_reopens_the_attempt_and_is_no_longer_counted()
    {
        var repair = AtCentre();
        var failed = repair.RecordFailure(Failure, Report(2));
        var attempt = repair.CurrentAttempt.ShouldNotBeNull();

        repair.Correct(failed.Seq, CorrectionReason, new RepairCorrection(ToStatus: RepairStatus.AtRepairCentre), Report(2));

        (repair.Status, repair.NotPossibleReason, repair.FailedAttempts).ShouldBe((RepairStatus.AtRepairCentre, (RepairNotPossibleReason?)null, 0));
        (attempt.Result, attempt.FailureReason).ShouldBe((AttemptResult.Open, (string?)null));
    }

    [Fact]
    public void Only_the_latest_status_change_can_be_sent_back_and_only_to_its_previous_status()
    {
        var repair = AtCentre();
        var handedOver = repair.Events.Single(e => e.Type == RepairEventType.HandedOver);
        var marked = repair.MarkReady(Report(2));

        Should.Throw<ArgumentException>(() => repair.Correct(
            handedOver.Seq, CorrectionReason, new RepairCorrection(ToStatus: RepairStatus.AwaitingHandover), Report(2))).ParamName
            .ShouldBe("toStatus");
        Should.Throw<ArgumentException>(() => repair.Correct(
            marked.Seq, CorrectionReason, new RepairCorrection(ToStatus: RepairStatus.AwaitingHandover), Report(2))).ParamName
            .ShouldBe("toStatus");
        repair.Status.ShouldBe(RepairStatus.ReadyForCollection);
    }

    [Theory]
    [InlineData("noReason", "reason")]
    [InlineData("unknownEntry", "correctsSeq")]
    [InlineData("systemEntry", "correctsSeq")]
    [InlineData("noValue", "correction")]
    [InlineData("twoValues", "correction")]
    [InlineData("dateOfAnotherEntry", "handedOverOn")]
    [InlineData("estimateOfAnotherEntry", "estimateAmount")]
    public void A_correction_needs_a_reason_an_entry_to_correct_and_one_value_that_fits_it(string request, string param)
    {
        var repair = AtCentre();
        var marked = repair.MarkReady(Report(2));
        var handedOver = repair.Events.Single(e => e.Type == RepairEventType.HandedOver);
        var suggested = repair.Events.Single(e => e.Type == RepairEventType.CentreSuggested);
        var events = repair.Events.Count;
        var date = new RepairCorrection(HandedOverOn: HandedOverOn.AddDays(-1));

        Action correct = request switch
        {
            "noReason" => () => repair.Correct(handedOver.Seq, " ", date, Report(2)),
            "unknownEntry" => () => repair.Correct(99, CorrectionReason, date, Report(2)),
            "systemEntry" => () => repair.Correct(suggested.Seq, CorrectionReason, date, Report(2)),
            "noValue" => () => repair.Correct(handedOver.Seq, CorrectionReason, new RepairCorrection(), Report(2)),
            "twoValues" => () => repair.Correct(
                handedOver.Seq, CorrectionReason, new RepairCorrection(HandedOverOn: HandedOverOn, CentreReference: "JOB-9"), Report(2)),
            "dateOfAnotherEntry" => () => repair.Correct(marked.Seq, CorrectionReason, date, Report(2)),
            "estimateOfAnotherEntry" => () => repair.Correct(handedOver.Seq, CorrectionReason, new RepairCorrection(EstimateAmount: 10m), Report(2)),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request, null),
        };

        Should.Throw<ArgumentException>(correct).ParamName.ShouldBe(param);
        (repair.Status, repair.Events.Count).ShouldBe((RepairStatus.ReadyForCollection, events));
        repair.ActiveHandover.ShouldNotBeNull().HandedOverOn.ShouldBe(HandedOverOn);
    }

    [Fact]
    public void A_collected_repair_cannot_be_corrected()
    {
        var repair = AtCentre();
        repair.MarkReady(Report(2));
        repair.RecordCollection(Report(3));
        var handedOver = repair.Events.Single(e => e.Type == RepairEventType.HandedOver);

        repair.AllowedEvents().ShouldNotContain(RepairEventType.Correction);
        Should.Throw<InvalidOperationException>(() => repair.Correct(
            handedOver.Seq, CorrectionReason, new RepairCorrection(HandedOverOn: HandedOverOn.AddDays(-1)), Report(4))).Message
            .ShouldContain(nameof(RepairStatus.Collected));
    }

    [Fact]
    public void Transfer_and_correction_are_allowed_where_the_table_lists_them()
    {
        var awaiting = Repair.Create(Guid.CreateVersion7(), Tenant, ClaimId, FirstCentre, CreatedAt);
        var atCentre = AtCentre();
        var notPossible = AtCentre();
        notPossible.RecordFailure(Failure, Report(2));

        awaiting.AllowedEvents().ShouldContain(RepairEventType.Transferred);
        atCentre.AllowedEvents().ShouldContain(RepairEventType.Transferred);
        notPossible.AllowedEvents().ShouldNotContain(RepairEventType.Transferred);
        new[] { awaiting, atCentre, notPossible }.ShouldAllBe(r => r.AllowedEvents().Contains(RepairEventType.Correction));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static JsonElement Payload(RepairEvent repairEvent) => JsonDocument.Parse(repairEvent.PayloadJson).RootElement.Clone();

    /// <summary>A report by <see cref="Actor"/> by phone, <paramref name="day"/> days after the hand-over.</summary>
    private static RepairReport Report(int day)
    {
        var at = new DateTimeOffset(HandedOverOn.AddDays(day).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        return new RepairReport(at, ReportSource.Phone, Actor, null, at.AddMinutes(5));
    }

    /// <summary>A repair handed over to <see cref="FirstCentre"/> on <see cref="HandedOverOn"/> with attempt 1 open.</summary>
    private static Repair AtCentre()
    {
        var repair = Repair.Create(Guid.CreateVersion7(), Tenant, ClaimId, FirstCentre, CreatedAt);
        repair.RecordHandover(HandedOverOn, "JOB-1001", Report(0));
        return repair;
    }

    /// <summary>As <see cref="AtCentre"/>, after one failure and a new attempt at the centre (quickstart R12).</summary>
    private static Repair AtCentreAfterOneFailure()
    {
        var repair = AtCentre();
        repair.RecordFailure(Failure, Report(2));
        repair.StartNewAttempt(ProductLocation.AtCentre, Report(3));
        return repair;
    }
}
