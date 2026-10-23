using System.Text.Json;

namespace Warranty.Domain.Fulfillment;

/// <summary>
/// The repair of an approved claim whose remedy is Repair (<c>claims.repairs</c>), one per claim. Owns
/// its hand-overs, attempts and append-only events; status changes follow the repair state machine in
/// data-model.md and go only through this aggregate (transitions are added in T238, T247 and T256).
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
}
