using System.Text.Json;
using Warranty.Domain.Claims;

namespace Warranty.Domain.Fulfillment;

/// <summary>
/// The repair of an approved claim whose remedy is Repair (<c>claims.repairs</c>), one per claim. Owns
/// its hand-overs, attempts and append-only events; status changes follow the repair state machine in
/// data-model.md and go only through this aggregate (US3 and US5 transitions are added in T247 and T256).
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

    // Transitions (data-model.md, Repair state machine). A refused transition throws an
    // InvalidClaimTransitionException naming the current status and leaves the repair unchanged.

    /// <summary>
    /// The event types allowed from the current status (drives <c>RepairView.allowedEvents</c> and the 409 detail).
    /// US2 events; T247 and T256 add the US3 and US5 events. A hand-over needs an active centre.
    /// </summary>
    public IReadOnlyList<RepairEventType> AllowedEvents() => Status switch
    {
        RepairStatus.AwaitingHandover when ActiveHandoverId is not null => [RepairEventType.CentreChanged, RepairEventType.HandedOver],
        RepairStatus.AwaitingHandover => [RepairEventType.CentreChanged],
        RepairStatus.AtRepairCentre => [RepairEventType.MarkedReady],
        RepairStatus.ReadyForCollection => [RepairEventType.Collected],
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
    /// <see cref="RepairEventType.EstimateReported"/> at the repair centre: stays <see cref="RepairStatus.AtRepairCentre"/>, or
    /// becomes <see cref="RepairStatus.RepairNotPossible"/> (<see cref="RepairNotPossibleReason.NotEconomical"/>) only when
    /// <paramref name="economicLimitAmount"/> is set and the estimate is strictly above it. Pending T247.
    /// </summary>
    public RepairEvent RecordEstimate(decimal estimateAmount, decimal? economicLimitAmount, RepairReport report)
        => throw new NotImplementedException($"Pending T247 ({estimateAmount}, {economicLimitAmount}, {report}).");

    /// <summary>
    /// <see cref="RepairEventType.RepairFailed"/>: <see cref="RepairStatus.AtRepairCentre"/> → <see cref="RepairStatus.RepairNotPossible"/>
    /// (<see cref="RepairNotPossibleReason.Failed"/>); reason required; <see cref="FailedAttempts"/> + 1. Pending T247.
    /// </summary>
    public RepairEvent RecordFailure(string reason, RepairReport report)
        => throw new NotImplementedException($"Pending T247 ({reason}, {report}).");

    /// <summary>
    /// <see cref="RepairEventType.NewAttemptStarted"/> after a failure below the limit (the limit is checked by the use
    /// case): → <see cref="RepairStatus.AtRepairCentre"/> with a new open attempt when the product stayed at the centre,
    /// or → <see cref="RepairStatus.AwaitingHandover"/> when it was returned to the customer. Pending T247.
    /// </summary>
    public RepairEvent StartNewAttempt(ProductLocation productLocation, RepairReport report)
        => throw new NotImplementedException($"Pending T247 ({productLocation}, {report}).");

    /// <summary>
    /// <see cref="RepairEventType.Transferred"/>: from <see cref="RepairStatus.AwaitingHandover"/> or
    /// <see cref="RepairStatus.AtRepairCentre"/> → <see cref="RepairStatus.AwaitingHandover"/> with a new active hand-over
    /// (<see cref="HandoverAssignment.Transfer"/>, reason required); the attempt count is unchanged. Pending T256.
    /// </summary>
    public RepairEvent Transfer(Guid serviceCenterId, string reason, RepairReport report)
        => throw new NotImplementedException($"Pending T256 ({serviceCenterId}, {reason}, {report}).");

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
