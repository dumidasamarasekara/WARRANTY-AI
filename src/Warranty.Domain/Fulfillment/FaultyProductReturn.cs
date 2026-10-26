namespace Warranty.Domain.Fulfillment;

/// <summary>
/// The return of the faulty product (<c>claims.faulty_product_returns</c>), at most one per replacement;
/// corrections go through replacement events. Either received by a replacement location (with the serial
/// seen there and whether it matches the claim's serial) or held by a repair centre since a 002 repair
/// hand-over (research R9). Created and changed only through <see cref="Replacement"/>.
/// </summary>
public sealed class FaultyProductReturn
{
    public const int MaxSerialLength = 100;

    public const int MaxNoteLength = 1000;

    private FaultyProductReturn()
    {
        RecordedBy = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ReplacementId { get; private set; }

    public DateOnly ReturnedOn { get; private set; }

    /// <summary>The location that received it; null when held by a repair centre.</summary>
    public Guid? ServiceCenterId { get; private set; }

    /// <summary>As reported by the location; required unless held by a repair centre. Staff-only.</summary>
    public string? SerialSeen { get; private set; }

    /// <summary>The <c>EvidenceMatchRules</c> comparison with the claim's serial (001 FR-016).</summary>
    public bool SerialMatches { get; private set; }

    /// <summary>The 002 repair hand-over; set when the product is held by a repair centre (FR-020).</summary>
    public Guid? RepairHandoverId { get; private set; }

    /// <summary>Staff-only note.</summary>
    public string? Note { get; private set; }

    /// <summary>Staff <c>sub</c> or <c>system</c>.</summary>
    public string RecordedBy { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    /// <summary>True when the product was not brought in but is held by a repair centre (research R9).</summary>
    public bool IsHeldByRepairCentre => RepairHandoverId.HasValue;

    internal static FaultyProductReturn Create(
        Guid tenantId,
        Guid replacementId,
        DateOnly returnedOn,
        Guid? serviceCenterId,
        string? serialSeen,
        bool serialMatches,
        Guid? repairHandoverId,
        string? note,
        string recordedBy,
        DateTimeOffset recordedAt)
    {
        if (tenantId == Guid.Empty || replacementId == Guid.Empty)
        {
            throw new ArgumentException("Tenant and replacement IDs are required.");
        }

        if (serviceCenterId == Guid.Empty || repairHandoverId == Guid.Empty)
        {
            throw new ArgumentException("A location or repair hand-over ID must not be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(recordedBy);

        var serial = string.IsNullOrWhiteSpace(serialSeen) ? null : serialSeen.Trim();
        if (repairHandoverId is null)
        {
            if (serviceCenterId is null || serial is null)
            {
                throw new ArgumentException("A returned product needs the receiving location and the serial seen.");
            }
        }
        else if (serviceCenterId is not null)
        {
            throw new ArgumentException("A product held by a repair centre has no receiving location.", nameof(serviceCenterId));
        }

        if (serial is { Length: > MaxSerialLength })
        {
            throw new ArgumentException($"The serial seen is at most {MaxSerialLength} characters.", nameof(serialSeen));
        }

        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote is { Length: > MaxNoteLength })
        {
            throw new ArgumentException($"A return note is at most {MaxNoteLength} characters.", nameof(note));
        }

        return new FaultyProductReturn
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ReplacementId = replacementId,
            ReturnedOn = returnedOn,
            ServiceCenterId = serviceCenterId,
            SerialSeen = serial,
            SerialMatches = serialMatches,
            RepairHandoverId = repairHandoverId,
            Note = trimmedNote,
            RecordedBy = recordedBy,
            RecordedAt = recordedAt,
        };
    }

    /// <summary>Applies a corrected return date (FR-017).</summary>
    internal void CorrectReturnedOn(DateOnly returnedOn) => ReturnedOn = returnedOn;

    /// <summary>Applies a corrected serial seen with its comparison with the claim's serial (FR-017, FR-019).</summary>
    internal void CorrectSerialSeen(string serialSeen, bool serialMatches)
    {
        var serial = string.IsNullOrWhiteSpace(serialSeen)
            ? throw new ArgumentException("The corrected serial seen is required.", nameof(serialSeen))
            : serialSeen.Trim();
        if (serial.Length > MaxSerialLength)
        {
            throw new ArgumentException($"The serial seen is at most {MaxSerialLength} characters.", nameof(serialSeen));
        }

        SerialSeen = serial;
        SerialMatches = serialMatches;
    }
}
