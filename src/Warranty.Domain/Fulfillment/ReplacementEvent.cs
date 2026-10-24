namespace Warranty.Domain.Fulfillment;

/// <summary>
/// One append-only event of a replacement (<c>audit.replacement_events</c>), numbered per replacement
/// without gaps. <see cref="Id"/> is assigned by the database. Created only through <see cref="Replacement"/>.
/// </summary>
public sealed class ReplacementEvent
{
    /// <summary>The <see cref="Actor"/> value of an event recorded by the system.</summary>
    public const string SystemActor = "system";

    private ReplacementEvent()
    {
        Actor = PayloadJson = string.Empty;
    }

    /// <summary>Database-assigned (bigserial); 0 until saved.</summary>
    public long Id { get; private init; }

    public Guid TenantId { get; private init; }

    public Guid ReplacementId { get; private init; }

    public Guid ClaimId { get; private init; }

    /// <summary>Per replacement, from 1, gap-free.</summary>
    public int Seq { get; private init; }

    public ReplacementEventType Type { get; private init; }

    public ReplacementStatus? FromStatus { get; private init; }

    public ReplacementStatus? ToStatus { get; private init; }

    /// <summary>When the location reported it (FR-015).</summary>
    public DateTimeOffset ReportedAt { get; private init; }

    /// <summary>Server time.</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    public ReportSource Source { get; private init; }

    /// <summary>Staff <c>sub</c> or <see cref="SystemActor"/>.</summary>
    public string Actor { get; private init; }

    /// <summary>Staff-only note.</summary>
    public string? Note { get; private init; }

    /// <summary>Event details (location, model, serial, reason, corrected values) as JSON.</summary>
    public string PayloadJson { get; private init; }

    /// <summary>The corrected event; set for <see cref="ReplacementEventType.Correction"/>.</summary>
    public long? CorrectsEventId { get; private init; }

    internal static ReplacementEvent Create(
        Guid tenantId,
        Guid replacementId,
        Guid claimId,
        int seq,
        ReplacementEventType type,
        ReplacementStatus? fromStatus,
        ReplacementStatus? toStatus,
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
        if ((type == ReplacementEventType.Correction) != correctsEventId.HasValue)
        {
            throw new ArgumentException("A correction, and only a correction, references the corrected event.", nameof(correctsEventId));
        }

        return new ReplacementEvent
        {
            TenantId = tenantId,
            ReplacementId = replacementId,
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
