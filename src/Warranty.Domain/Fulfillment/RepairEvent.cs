namespace Warranty.Domain.Fulfillment;

/// <summary>
/// One append-only event of a repair (<c>audit.repair_events</c>), numbered per repair without gaps.
/// <see cref="Id"/> is assigned by the database. Created only through <see cref="Repair"/>.
/// </summary>
public sealed class RepairEvent
{
    /// <summary>The <see cref="Actor"/> value of an event recorded by the system.</summary>
    public const string SystemActor = "system";

    private RepairEvent()
    {
        Actor = PayloadJson = string.Empty;
    }

    /// <summary>Database-assigned (bigserial); 0 until saved.</summary>
    public long Id { get; private init; }

    public Guid TenantId { get; private init; }

    public Guid RepairId { get; private init; }

    public Guid ClaimId { get; private init; }

    /// <summary>Per repair, from 1, gap-free.</summary>
    public int Seq { get; private init; }

    public RepairEventType Type { get; private init; }

    public RepairStatus? FromStatus { get; private init; }

    public RepairStatus? ToStatus { get; private init; }

    /// <summary>When the repair centre reported it (FR-025).</summary>
    public DateTimeOffset ReportedAt { get; private init; }

    /// <summary>Server time.</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    public ReportSource Source { get; private init; }

    /// <summary>Staff <c>sub</c> or <see cref="SystemActor"/>.</summary>
    public string Actor { get; private init; }

    /// <summary>Staff-only note.</summary>
    public string? Note { get; private init; }

    /// <summary>Event details (centre IDs, estimate, reference, corrected values) as JSON.</summary>
    public string PayloadJson { get; private init; }

    /// <summary>The corrected event; set for <see cref="RepairEventType.Correction"/>.</summary>
    public long? CorrectsEventId { get; private init; }

    internal static RepairEvent Create(
        Guid tenantId,
        Guid repairId,
        Guid claimId,
        int seq,
        RepairEventType type,
        RepairStatus? fromStatus,
        RepairStatus? toStatus,
        DateTimeOffset reportedAt,
        DateTimeOffset recordedAt,
        ReportSource source,
        string actor,
        string? note,
        string? payloadJson,
        long? correctsEventId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seq);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if ((type == RepairEventType.Correction) != correctsEventId.HasValue)
        {
            throw new ArgumentException("A correction, and only a correction, references the corrected event.", nameof(correctsEventId));
        }

        return new RepairEvent
        {
            TenantId = tenantId,
            RepairId = repairId,
            ClaimId = claimId,
            Seq = seq,
            Type = type,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            ReportedAt = reportedAt,
            RecordedAt = recordedAt,
            Source = source,
            Actor = actor,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            CorrectsEventId = correctsEventId,
        };
    }
}
