using System.Globalization;
using System.Text.Json;
using Warranty.Domain.Claims;

namespace Warranty.Domain.Fulfillment;

/// <summary>
/// The repair of an approved claim whose remedy is Repair (<c>claims.repairs</c>), one per claim. Owns
/// its hand-overs, attempts and append-only events; status changes follow the repair state machine in
/// data-model.md and go only through this aggregate.
/// </summary>
public sealed class Repair
{
    private readonly List<RepairHandover> _handovers = [];
    private readonly List<RepairAttempt> _attempts = [];
    private readonly List<RepairEvent> _events = [];

    private Repair()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ClaimId { get; private set; }

    public RepairStatus Status { get; private set; }

    /// <summary>Set together with <see cref="RepairStatus.RepairNotPossible"/>.</summary>
    public RepairNotPossibleReason? NotPossibleReason { get; private set; }

    /// <summary>Null while no centre is assigned (no eligible centre was found).</summary>
    public Guid? ActiveHandoverId { get; private set; }

    /// <summary>Null until the first hand-over opens attempt 1.</summary>
    public Guid? CurrentAttemptId { get; private set; }

    /// <summary>Attempts ended as <see cref="AttemptResult.Failed"/> (FR-029).</summary>
    public int FailedAttempts { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<RepairHandover> Handovers => _handovers;

    public IReadOnlyList<RepairAttempt> Attempts => _attempts;

    /// <summary>The events of this repair in <see cref="RepairEvent.Seq"/> order as loaded or appended.</summary>
    public IReadOnlyList<RepairEvent> Events => _events;

    public RepairHandover? ActiveHandover =>
        ActiveHandoverId is { } id ? _handovers.SingleOrDefault(h => h.Id == id) : null;

    public RepairAttempt? CurrentAttempt =>
        CurrentAttemptId is { } id ? _attempts.SingleOrDefault(a => a.Id == id) : null;

    /// <summary>
    /// Creates the repair in <see cref="RepairStatus.AwaitingHandover"/>. When a centre is suggested it
    /// becomes the active hand-over (<see cref="HandoverAssignment.Suggested"/>, recorded by the system)
    /// and a <see cref="RepairEventType.CentreSuggested"/> event is appended; otherwise no centre is assigned.
    /// </summary>
    public static Repair Create(Guid id, Guid tenantId, Guid claimId, Guid? suggestedServiceCenterId, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || claimId == Guid.Empty)
        {
            throw new ArgumentException("Repair, tenant and claim IDs are required.");
        }

        if (suggestedServiceCenterId == Guid.Empty)
        {
            throw new ArgumentException("A suggested centre ID must not be empty.", nameof(suggestedServiceCenterId));
        }

        var repair = new Repair
        {
            Id = id,
            TenantId = tenantId,
            ClaimId = claimId,
            Status = RepairStatus.AwaitingHandover,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };

        if (suggestedServiceCenterId is { } centreId)
        {
            var handover = repair.AddHandover(
                centreId, HandoverAssignment.Suggested, reason: null, RepairHandover.SystemRecorder, createdAt);
            repair.AppendEvent(
                RepairEventType.CentreSuggested,
                fromStatus: null,
                toStatus: RepairStatus.AwaitingHandover,
                reportedAt: createdAt,
                recordedAt: createdAt,
                ReportSource.System,
                RepairEvent.SystemActor,
                note: null,
                payloadJson: JsonSerializer.Serialize(new { serviceCenterId = centreId, handoverId = handover.Id }));
        }

        return repair;
    }

    /// <summary>
    /// The hand-over through which the repair centre holds the faulty product (003 research R9), or null: the repair is at the
    /// centre or ended there as not possible, its active hand-over has a hand-over date, and the product was not returned to
    /// the customer by a <see cref="RepairEventType.NewAttemptStarted"/> after that hand-over. A replacement that follows
    /// starts with the return recorded by the system, referring to this hand-over (003 FR-020).
    /// </summary>
    public RepairHandover? HandoverHoldingProduct()
    {
        if (Status is not (RepairStatus.AtRepairCentre or RepairStatus.RepairNotPossible)
            || ActiveHandover is not { HandedOverOn: not null } handover)
        {
            return null;
        }

        var lastMove = _events.LastOrDefault(e => e.Type is RepairEventType.HandedOver or RepairEventType.NewAttemptStarted);
        if (lastMove is not { Type: RepairEventType.NewAttemptStarted })
        {
            return handover;
        }

        using var payload = JsonDocument.Parse(lastMove.PayloadJson);
        var returnedToCustomer = payload.RootElement.TryGetProperty("productLocation", out var productLocation)
            && productLocation.GetString() == nameof(ProductLocation.ReturnedToCustomer);
        return returnedToCustomer ? null : handover;
    }

    // Transitions (data-model.md, Repair state machine). A refused transition throws an
    // InvalidClaimTransitionException naming the current status and leaves the repair unchanged.

    /// <summary>
    /// The event types allowed from the current status (drives <c>RepairView.allowedEvents</c> and the 409 detail).
    /// A hand-over and a transfer need an active centre; a new attempt follows only a failure (whether the attempt limit allows it is
    /// decided by the remedy evaluation of the use case); a correction is allowed in every status except the final Collected.
    /// </summary>
    public IReadOnlyList<RepairEventType> AllowedEvents() => Status switch
    {
        RepairStatus.AwaitingHandover when ActiveHandoverId is not null =>
            [RepairEventType.CentreChanged, RepairEventType.HandedOver, RepairEventType.Transferred, RepairEventType.Correction],
        RepairStatus.AwaitingHandover => [RepairEventType.CentreChanged, RepairEventType.Correction],
        RepairStatus.AtRepairCentre =>
        [
            RepairEventType.EstimateReported, RepairEventType.MarkedReady, RepairEventType.RepairFailed, RepairEventType.Transferred,
            RepairEventType.Correction,
        ],
        RepairStatus.ReadyForCollection => [RepairEventType.Collected, RepairEventType.Correction],
        RepairStatus.RepairNotPossible when NotPossibleReason == RepairNotPossibleReason.Failed =>
            [RepairEventType.NewAttemptStarted, RepairEventType.Correction],
        RepairStatus.RepairNotPossible => [RepairEventType.Correction],
        _ => [],
    };

    /// <summary>
    /// <see cref="RepairEventType.CentreChanged"/>: <see cref="RepairStatus.AwaitingHandover"/> stays; the staff-chosen
    /// centre becomes the active hand-over (<see cref="HandoverAssignment.ChosenByStaff"/>, reason required). Whether the
    /// centre is an eligible centre of the tenant is checked by the use case.
    /// </summary>
    public RepairEvent ChangeCentre(Guid serviceCenterId, string reason, RepairReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (serviceCenterId == Guid.Empty)
        {
            throw new ArgumentException("A repair centre ID is required.", nameof(serviceCenterId));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to change the repair centre.", nameof(reason));
        }

        Require(RepairEventType.CentreChanged, RepairStatus.AwaitingHandover);

        var previous = ActiveHandover?.ServiceCenterId;
        var handover = AddHandover(serviceCenterId, HandoverAssignment.ChosenByStaff, reason, report.Actor, report.RecordedAt);
        return Append(
            RepairEventType.CentreChanged,
            RepairStatus.AwaitingHandover,
            RepairStatus.AwaitingHandover,
            report,
            new Dictionary<string, object?>
            {
                ["serviceCenterId"] = serviceCenterId,
                ["previousServiceCenterId"] = previous,
                ["handoverId"] = handover.Id,
                ["reason"] = handover.Reason,
            });
    }

    /// <summary>
    /// <see cref="RepairEventType.HandedOver"/>: <see cref="RepairStatus.AwaitingHandover"/> → <see cref="RepairStatus.AtRepairCentre"/>;
    /// needs an active centre; records the date and centre reference on the active hand-over and opens attempt 1
    /// when no attempt is open.
    /// </summary>
    public RepairEvent RecordHandover(DateOnly handedOverOn, string? centreReference, RepairReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Require(RepairEventType.HandedOver, RepairStatus.AwaitingHandover);
        var handover = ActiveHandover
            ?? throw new InvalidClaimTransitionException(
                $"Cannot record {RepairEventType.HandedOver} on a repair in status {Status} without an active repair centre; "
                + $"allowed events: {Describe(AllowedEvents())}.");

        handover.RecordHandedOver(handedOverOn, centreReference, report.Note);
        var attempt = CurrentAttempt is { IsOpen: true } open ? open : StartAttempt(handover.ServiceCenterId, report.RecordedAt);
        Status = RepairStatus.AtRepairCentre;
        return Append(
            RepairEventType.HandedOver,
            RepairStatus.AwaitingHandover,
            RepairStatus.AtRepairCentre,
            report,
            new Dictionary<string, object?>
            {
                ["serviceCenterId"] = handover.ServiceCenterId,
                ["handoverId"] = handover.Id,
                ["handedOverOn"] = handedOverOn,
                ["centreReference"] = handover.CentreReference,
                ["attemptId"] = attempt.Id,
                ["attemptSeq"] = attempt.Seq,
            });
    }

    /// <summary>
    /// <see cref="RepairEventType.MarkedReady"/>: <see cref="RepairStatus.AtRepairCentre"/> → <see cref="RepairStatus.ReadyForCollection"/>;
    /// the current attempt ends as <see cref="AttemptResult.Ready"/>.
    /// </summary>
    public RepairEvent MarkReady(RepairReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Require(RepairEventType.MarkedReady, RepairStatus.AtRepairCentre);
        var attempt = CurrentAttempt is { IsOpen: true } open
            ? open
            : throw new InvalidClaimTransitionException(
                $"Cannot record {RepairEventType.MarkedReady} on a repair in status {Status} without an open attempt.");

        attempt.MarkReady(report.RecordedAt);
        Status = RepairStatus.ReadyForCollection;
        return Append(
            RepairEventType.MarkedReady,
            RepairStatus.AtRepairCentre,
            RepairStatus.ReadyForCollection,
            report,
            new Dictionary<string, object?> { ["attemptId"] = attempt.Id, ["attemptSeq"] = attempt.Seq });
    }

    /// <summary>
    /// <see cref="RepairEventType.Collected"/>: <see cref="RepairStatus.ReadyForCollection"/> → <see cref="RepairStatus.Collected"/>
    /// (final); the current attempt's result becomes <see cref="AttemptResult.Collected"/>.
    /// </summary>
    public RepairEvent RecordCollection(RepairReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Require(RepairEventType.Collected, RepairStatus.ReadyForCollection);
        var attempt = CurrentAttempt is { Result: AttemptResult.Ready } ready
            ? ready
            : throw new InvalidClaimTransitionException(
                $"Cannot record {RepairEventType.Collected} on a repair in status {Status} without a ready attempt.");

        attempt.MarkCollected();
        Status = RepairStatus.Collected;
        return Append(
            RepairEventType.Collected,
            RepairStatus.ReadyForCollection,
            RepairStatus.Collected,
            report,
            new Dictionary<string, object?> { ["attemptId"] = attempt.Id, ["attemptSeq"] = attempt.Seq });
    }

    /// <summary>
    /// <see cref="RepairEventType.EstimateReported"/> at the repair centre: the estimate is kept on the open attempt and the repair
    /// stays <see cref="RepairStatus.AtRepairCentre"/>, or becomes <see cref="RepairStatus.RepairNotPossible"/>
    /// (<see cref="RepairNotPossibleReason.NotEconomical"/>, the attempt ends as <see cref="AttemptResult.NotEconomical"/>) only when
    /// <paramref name="economicLimitAmount"/> is set and the estimate is strictly above it (FR-004). Never counted as a failure (FR-029).
    /// </summary>
    public RepairEvent RecordEstimate(decimal estimateAmount, decimal? economicLimitAmount, RepairReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentOutOfRangeException.ThrowIfNegative(estimateAmount);
        Require(RepairEventType.EstimateReported, RepairStatus.AtRepairCentre);
        var attempt = OpenAttempt(RepairEventType.EstimateReported);

        var notEconomical = economicLimitAmount is { } limit && estimateAmount > limit;
        attempt.ReportEstimate(estimateAmount);
        if (notEconomical)
        {
            attempt.EndNotEconomical(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The estimate of {estimateAmount:0.00} is above the economic limit of {economicLimitAmount:0.00}."),
                report.RecordedAt);
            Status = RepairStatus.RepairNotPossible;
            NotPossibleReason = RepairNotPossibleReason.NotEconomical;
        }

        return Append(
            RepairEventType.EstimateReported,
            RepairStatus.AtRepairCentre,
            Status,
            report,
            new Dictionary<string, object?>
            {
                ["attemptId"] = attempt.Id,
                ["attemptSeq"] = attempt.Seq,
                ["estimateAmount"] = estimateAmount,
                ["economicLimitAmount"] = economicLimitAmount,
                ["notEconomical"] = notEconomical,
            });
    }

    /// <summary>
    /// <see cref="RepairEventType.RepairFailed"/>: <see cref="RepairStatus.AtRepairCentre"/> → <see cref="RepairStatus.RepairNotPossible"/>
    /// (<see cref="RepairNotPossibleReason.Failed"/>); reason required; the open attempt ends as <see cref="AttemptResult.Failed"/> and
    /// <see cref="FailedAttempts"/> + 1 (FR-029). Whether the limit is reached is decided by the remedy evaluation of the use case.
    /// </summary>
    public RepairEvent RecordFailure(string reason, RepairReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required for a failed repair.", nameof(reason));
        }

        Require(RepairEventType.RepairFailed, RepairStatus.AtRepairCentre);
        var attempt = OpenAttempt(RepairEventType.RepairFailed);

        attempt.EndFailed(reason.Trim(), report.RecordedAt);
        FailedAttempts++;
        Status = RepairStatus.RepairNotPossible;
        NotPossibleReason = RepairNotPossibleReason.Failed;
        return Append(
            RepairEventType.RepairFailed,
            RepairStatus.AtRepairCentre,
            RepairStatus.RepairNotPossible,
            report,
            new Dictionary<string, object?>
            {
                ["attemptId"] = attempt.Id,
                ["attemptSeq"] = attempt.Seq,
                ["reason"] = attempt.FailureReason,
                ["failedAttempts"] = FailedAttempts,
            });
    }

    /// <summary>
    /// <see cref="RepairEventType.NewAttemptStarted"/> after a failure below the limit (the limit is checked by the use case):
    /// → <see cref="RepairStatus.AtRepairCentre"/> with the next attempt open at the active centre when the product stayed there,
    /// or → <see cref="RepairStatus.AwaitingHandover"/> when it was returned to the customer (the next hand-over opens the attempt).
    /// The failed attempts are kept and the count is unchanged.
    /// </summary>
    public RepairEvent StartNewAttempt(ProductLocation productLocation, RepairReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!Enum.IsDefined(productLocation))
        {
            throw new ArgumentException("The product location must be AtCentre or ReturnedToCustomer.", nameof(productLocation));
        }

        if (Status != RepairStatus.RepairNotPossible || NotPossibleReason != RepairNotPossibleReason.Failed)
        {
            throw new InvalidClaimTransitionException(
                $"Cannot record {RepairEventType.NewAttemptStarted} on a repair in status {Status}"
                + (NotPossibleReason is { } reason ? $" ({reason})" : string.Empty)
                + $"; allowed events: {Describe(AllowedEvents())}.");
        }

        var handover = ActiveHandover
            ?? throw new InvalidClaimTransitionException(
                $"Cannot record {RepairEventType.NewAttemptStarted} on a repair in status {Status} without an active repair centre.");

        RepairAttempt? attempt = null;
        if (productLocation == ProductLocation.AtCentre)
        {
            attempt = StartAttempt(handover.ServiceCenterId, report.RecordedAt);
            Status = RepairStatus.AtRepairCentre;
        }
        else
        {
            Status = RepairStatus.AwaitingHandover;
        }

        NotPossibleReason = null;
        return Append(
            RepairEventType.NewAttemptStarted,
            RepairStatus.RepairNotPossible,
            Status,
            report,
            new Dictionary<string, object?>
            {
                ["productLocation"] = productLocation.ToString(),
                ["serviceCenterId"] = handover.ServiceCenterId,
                ["attemptId"] = attempt?.Id,
                ["attemptSeq"] = attempt?.Seq,
                ["failedAttempts"] = FailedAttempts,
            });
    }

    /// <summary>
    /// <see cref="RepairEventType.Transferred"/>: from <see cref="RepairStatus.AwaitingHandover"/> or
    /// <see cref="RepairStatus.AtRepairCentre"/> → <see cref="RepairStatus.AwaitingHandover"/> with a new active hand-over at another
    /// centre (<see cref="HandoverAssignment.Transfer"/>, reason required). The old hand-over stays in the history; nothing ends: an
    /// open attempt continues at the new centre, so the attempt count is unchanged (FR-026). Whether the centre is an eligible
    /// centre of the tenant is checked by the use case.
    /// </summary>
    public RepairEvent Transfer(Guid serviceCenterId, string reason, RepairReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (serviceCenterId == Guid.Empty)
        {
            throw new ArgumentException("A repair centre ID is required.", nameof(serviceCenterId));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to transfer the repair.", nameof(reason));
        }

        if (Status is not (RepairStatus.AwaitingHandover or RepairStatus.AtRepairCentre))
        {
            throw new InvalidClaimTransitionException(
                $"Cannot record {RepairEventType.Transferred} on a repair in status {Status}; allowed events: {Describe(AllowedEvents())}.");
        }

        var previous = ActiveHandover
            ?? throw new InvalidClaimTransitionException(
                $"Cannot record {RepairEventType.Transferred} on a repair in status {Status} without an active repair centre; "
                + $"allowed events: {Describe(AllowedEvents())}.");
        if (previous.ServiceCenterId == serviceCenterId)
        {
            throw new ArgumentException("A transfer needs another repair centre than the active one.", nameof(serviceCenterId));
        }

        var from = Status;
        var handover = AddHandover(serviceCenterId, HandoverAssignment.Transfer, reason, report.Actor, report.RecordedAt);
        var attempt = CurrentAttempt is { IsOpen: true } open ? open : null;
        attempt?.MoveTo(serviceCenterId);
        Status = RepairStatus.AwaitingHandover;
        return Append(
            RepairEventType.Transferred,
            from,
            RepairStatus.AwaitingHandover,
            report,
            new Dictionary<string, object?>
            {
                ["serviceCenterId"] = serviceCenterId,
                ["previousServiceCenterId"] = previous.ServiceCenterId,
                ["handoverId"] = handover.Id,
                ["reason"] = handover.Reason,
                ["attemptId"] = attempt?.Id,
                ["attemptSeq"] = attempt?.Seq,
            });
    }

    /// <summary>
    /// <see cref="RepairEventType.Correction"/> of the staff entry with sequence number <paramref name="correctsSeq"/> (any status
    /// except <see cref="RepairStatus.Collected"/>, reason required): applies the one corrected value of <paramref name="correction"/>
    /// and appends a correction that references the entry; the corrected entry itself is never changed (FR-027). A hand-over date or
    /// centre reference corrects a <see cref="RepairEventType.HandedOver"/> entry's hand-over, an amount corrects an
    /// <see cref="RepairEventType.EstimateReported"/> entry's attempt. A status correction sends the latest status change
    /// (<see cref="RepairEventType.MarkedReady"/>, <see cref="RepairEventType.RepairFailed"/> or an uneconomical estimate) back to its
    /// previous status and reopens the attempt it ended (a corrected failure is no longer counted), so a ready mark recorded again
    /// keeps the same attempt and the same ready notification. Whether the remedy allows sending a failure back is checked by the
    /// use case. An invalid correction throws an <see cref="ArgumentException"/> naming the request field.
    /// </summary>
    public RepairEvent Correct(int correctsSeq, string reason, RepairCorrection correction, RepairReport report)
    {
        ArgumentNullException.ThrowIfNull(correction);
        ArgumentNullException.ThrowIfNull(report);
        if (Status == RepairStatus.Collected)
        {
            throw new InvalidClaimTransitionException(
                $"Cannot record {RepairEventType.Correction} on a repair in status {Status}; allowed events: {Describe(AllowedEvents())}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required for a correction.", nameof(reason));
        }

        var corrected = _events.SingleOrDefault(e => e.Seq == correctsSeq)
            ?? throw new ArgumentException($"The repair has no entry {correctsSeq}.", nameof(correctsSeq));
        if (corrected.Actor == RepairEvent.SystemActor || corrected.Type == RepairEventType.Correction)
        {
            throw new ArgumentException(
                "Only an entry recorded by staff can be corrected; to correct a correction, correct the original entry again.",
                nameof(correctsSeq));
        }

        if (correction.ValueCount != 1)
        {
            throw new ArgumentException(
                "A correction sets exactly one corrected value: handedOverOn, centreReference, estimateAmount or toStatus.",
                nameof(correction));
        }

        var from = Status;
        var payload = new Dictionary<string, object?>
        {
            ["correctsSeq"] = corrected.Seq,
            ["correctedType"] = corrected.Type.ToString(),
            ["reason"] = reason.Trim(),
        };

        if (correction.HandedOverOn is { } handedOverOn)
        {
            var handover = HandoverOf(corrected, nameof(RepairCorrection.HandedOverOn));
            payload["previousHandedOverOn"] = handover.HandedOverOn;
            payload["handedOverOn"] = handedOverOn;
            handover.CorrectHandedOverOn(handedOverOn);
        }
        else if (correction.CentreReference is { } centreReference)
        {
            var handover = HandoverOf(corrected, nameof(RepairCorrection.CentreReference));
            payload["previousCentreReference"] = handover.CentreReference;
            handover.CorrectCentreReference(centreReference);
            payload["centreReference"] = handover.CentreReference;
        }
        else if (correction.EstimateAmount is { } estimateAmount)
        {
            const string field = "estimateAmount";
            if (corrected.Type != RepairEventType.EstimateReported)
            {
                throw new ArgumentException($"An amount corrects an {RepairEventType.EstimateReported} entry, not {corrected.Type}.", field);
            }

            if (estimateAmount < 0)
            {
                throw new ArgumentException("An estimate cannot be negative.", field);
            }

            var attempt = AttemptOf(corrected, field);
            payload["previousEstimateAmount"] = attempt.EstimateAmount;
            payload["estimateAmount"] = estimateAmount;
            attempt.CorrectEstimate(estimateAmount);
        }
        else
        {
            SendBack(corrected, correction.ToStatus!.Value, payload);
        }

        if (!string.IsNullOrWhiteSpace(report.ActorName))
        {
            payload["actorName"] = report.ActorName;
        }

        return AppendEvent(
            RepairEventType.Correction, from, Status, report.ReportedAt, report.RecordedAt, report.Source, report.Actor, report.Note,
            JsonSerializer.Serialize(payload), corrected.Id);
    }

    /// <summary>The next gap-free event sequence number.</summary>
    internal int NextEventSeq => _events.Count == 0 ? 1 : _events.Max(e => e.Seq) + 1;

    /// <summary>Deactivates the current hand-over (if any) and makes a new one active.</summary>
    internal RepairHandover AddHandover(
        Guid serviceCenterId, HandoverAssignment assignment, string? reason, string recordedBy, DateTimeOffset recordedAt)
    {
        var handover = RepairHandover.Create(TenantId, Id, serviceCenterId, assignment, reason, recordedBy, recordedAt);
        ActiveHandover?.Deactivate();
        _handovers.Add(handover);
        ActiveHandoverId = handover.Id;
        UpdatedAt = recordedAt;
        return handover;
    }

    /// <summary>Opens the next attempt at <paramref name="serviceCenterId"/> and makes it current.</summary>
    internal RepairAttempt StartAttempt(Guid serviceCenterId, DateTimeOffset startedAt)
    {
        if (CurrentAttempt is { IsOpen: true })
        {
            throw new InvalidOperationException("The current repair attempt is still open.");
        }

        var seq = _attempts.Count == 0 ? 1 : _attempts.Max(a => a.Seq) + 1;
        var attempt = RepairAttempt.Start(TenantId, Id, seq, serviceCenterId, startedAt);
        _attempts.Add(attempt);
        CurrentAttemptId = attempt.Id;
        UpdatedAt = startedAt;
        return attempt;
    }

    /// <summary>Appends an event with the next gap-free sequence number.</summary>
    internal RepairEvent AppendEvent(
        RepairEventType type,
        RepairStatus? fromStatus,
        RepairStatus? toStatus,
        DateTimeOffset reportedAt,
        DateTimeOffset recordedAt,
        ReportSource source,
        string actor,
        string? note,
        string? payloadJson,
        long? correctsEventId = null)
    {
        var repairEvent = RepairEvent.Create(
            TenantId, Id, ClaimId, NextEventSeq, type, fromStatus, toStatus, reportedAt, recordedAt, source, actor, note,
            payloadJson, correctsEventId);
        _events.Add(repairEvent);
        UpdatedAt = recordedAt;
        return repairEvent;
    }

    /// <summary>The allowed events as listed in a refusal.</summary>
    internal static string Describe(IReadOnlyList<RepairEventType> events)
        => events.Count == 0 ? "none" : string.Join(", ", events);

    /// <summary>
    /// Sends the latest status change <paramref name="corrected"/> back to its previous status <paramref name="toStatus"/> and reopens
    /// the attempt it ended; a corrected failure is no longer counted.
    /// </summary>
    private void SendBack(RepairEvent corrected, RepairStatus toStatus, Dictionary<string, object?> payload)
    {
        const string field = "toStatus";
        var latest = _events.Where(e => e.FromStatus is not null && e.FromStatus != e.ToStatus).MaxBy(e => e.Seq);
        if (latest is null || latest.Seq != corrected.Seq || corrected.ToStatus != Status || corrected.FromStatus != toStatus)
        {
            throw new ArgumentException(
                "Only the latest status change can be corrected, and only back to the status before it.", field);
        }

        if (corrected.Type is not (RepairEventType.MarkedReady or RepairEventType.RepairFailed or RepairEventType.EstimateReported))
        {
            throw new ArgumentException($"A {corrected.Type} entry cannot be sent back; correct its values instead.", field);
        }

        var attempt = AttemptOf(corrected, field);
        if (attempt.Id != CurrentAttemptId)
        {
            throw new ArgumentException("Only the current attempt can be reopened.", field);
        }

        if (attempt.Result == AttemptResult.Failed)
        {
            FailedAttempts--;
        }

        payload["previousStatus"] = Status.ToString();
        payload["toStatus"] = toStatus.ToString();
        payload["attemptId"] = attempt.Id;
        attempt.Reopen();
        Status = toStatus;
        NotPossibleReason = null;
    }

    /// <summary>The hand-over a <see cref="RepairEventType.HandedOver"/> entry recorded (its payload's <c>handoverId</c>).</summary>
    private RepairHandover HandoverOf(RepairEvent corrected, string field)
    {
        var name = JsonNamingPolicy.CamelCase.ConvertName(field);
        if (corrected.Type != RepairEventType.HandedOver)
        {
            throw new ArgumentException($"A {name} corrects a {RepairEventType.HandedOver} entry, not {corrected.Type}.", name);
        }

        var id = PayloadGuid(corrected, "handoverId");
        return _handovers.SingleOrDefault(h => h.Id == id)
            ?? throw new ArgumentException($"The hand-over of entry {corrected.Seq} is not known.", name);
    }

    /// <summary>The attempt an entry recorded (its payload's <c>attemptId</c>).</summary>
    private RepairAttempt AttemptOf(RepairEvent corrected, string field)
    {
        var id = PayloadGuid(corrected, "attemptId");
        return _attempts.SingleOrDefault(a => a.Id == id)
            ?? throw new ArgumentException($"The attempt of entry {corrected.Seq} is not known.", field);
    }

    private static Guid? PayloadGuid(RepairEvent repairEvent, string property)
    {
        using var payload = JsonDocument.Parse(repairEvent.PayloadJson);
        return payload.RootElement.ValueKind == JsonValueKind.Object
            && payload.RootElement.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.TryGetGuid(out var id)
                ? id
                : null;
    }

    /// <summary>The open attempt of a repair at the centre; refuses <paramref name="type"/> when none is open.</summary>
    private RepairAttempt OpenAttempt(RepairEventType type)
        => CurrentAttempt is { IsOpen: true } open
            ? open
            : throw new InvalidClaimTransitionException($"Cannot record {type} on a repair in status {Status} without an open attempt.");
    /// <summary>Refuses <paramref name="type"/> unless the repair is in <paramref name="from"/>, naming the status and the allowed events.</summary>
    private void Require(RepairEventType type, RepairStatus from)
    {
        if (Status != from)
        {
            throw new InvalidClaimTransitionException(
                $"Cannot record {type} on a repair in status {Status}; allowed events: {Describe(AllowedEvents())}.");
        }
    }

    /// <summary>Appends a staff-reported event; the payload also carries the staff member's display name when known.</summary>
    private RepairEvent Append(
        RepairEventType type, RepairStatus fromStatus, RepairStatus toStatus, RepairReport report, Dictionary<string, object?> payload)
    {
        if (!string.IsNullOrWhiteSpace(report.ActorName))
        {
            payload["actorName"] = report.ActorName;
        }

        return AppendEvent(
            type, fromStatus, toStatus, report.ReportedAt, report.RecordedAt, report.Source, report.Actor, report.Note,
            JsonSerializer.Serialize(payload));
    }
}
