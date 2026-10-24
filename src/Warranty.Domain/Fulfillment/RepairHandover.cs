namespace Warranty.Domain.Fulfillment;

/// <summary>
/// Assignment of a repair to one repair centre (<c>claims.repair_handovers</c>). At most one hand-over
/// per repair is active; a centre change or transfer deactivates it and adds a new one. Created and
/// changed only through <see cref="Repair"/>.
/// </summary>
public sealed class RepairHandover
{
    /// <summary>The <see cref="RecordedBy"/> value of a hand-over suggested by the system.</summary>
    public const string SystemRecorder = "system";

    public const int MaxCentreReferenceLength = 100;

    public const int MaxNoteLength = 1000;

    private RepairHandover()
    {
        RecordedBy = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid RepairId { get; private set; }

    public Guid ServiceCenterId { get; private set; }

    public HandoverAssignment Assignment { get; private set; }

    /// <summary>Null until the hand-over to the centre is recorded.</summary>
    public DateOnly? HandedOverOn { get; private set; }

    /// <summary>The centre's own job reference; staff-only.</summary>
    public string? CentreReference { get; private set; }

    /// <summary>Staff-only note.</summary>
    public string? Note { get; private set; }

    /// <summary>Required for <see cref="HandoverAssignment.ChosenByStaff"/> and <see cref="HandoverAssignment.Transfer"/>.</summary>
    public string? Reason { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Staff <c>sub</c>, or <see cref="SystemRecorder"/> for a suggestion.</summary>
    public string RecordedBy { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    internal static RepairHandover Create(
        Guid tenantId,
        Guid repairId,
        Guid serviceCenterId,
        HandoverAssignment assignment,
        string? reason,
        string recordedBy,
        DateTimeOffset recordedAt)
    {
        if (tenantId == Guid.Empty || repairId == Guid.Empty || serviceCenterId == Guid.Empty)
        {
            throw new ArgumentException("Tenant, repair and service-centre IDs are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(recordedBy);
        if (assignment != HandoverAssignment.Suggested && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException($"A reason is required for a {assignment} hand-over.", nameof(reason));
        }

        return new RepairHandover
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            RepairId = repairId,
            ServiceCenterId = serviceCenterId,
            Assignment = assignment,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            IsActive = true,
            RecordedBy = recordedBy,
            RecordedAt = recordedAt,
        };
    }

    /// <summary>Ends this assignment (a centre change or transfer replaces it).</summary>
    internal void Deactivate() => IsActive = false;

    /// <summary>Records that the product was handed over to the centre, with the centre's job reference and a note.</summary>
    internal void RecordHandedOver(DateOnly handedOverOn, string? centreReference, string? note)
    {
        var reference = string.IsNullOrWhiteSpace(centreReference) ? null : centreReference.Trim();
        if (reference is { Length: > MaxCentreReferenceLength })
        {
            throw new ArgumentException(
                $"A centre reference has at most {MaxCentreReferenceLength} characters.", nameof(centreReference));
        }

        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote is { Length: > MaxNoteLength })
        {
            throw new ArgumentException($"A note has at most {MaxNoteLength} characters.", nameof(note));
        }

        HandedOverOn = handedOverOn;
        CentreReference = reference;
        Note = trimmedNote;
    }
}
