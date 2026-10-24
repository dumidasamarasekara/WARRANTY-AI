namespace Warranty.Domain.Fulfillment;

/// <summary>
/// The corrected value of a <see cref="ReplacementEventType.Correction"/> (003 contracts <c>ReplacementEventRequest</c>,
/// FR-017): exactly one of the return date or serial seen of a <see cref="ReplacementEventType.ReturnRecorded"/> entry,
/// the serial number of a <see cref="ReplacementEventType.UnitReserved"/> entry, the hand-over date of a
/// <see cref="ReplacementEventType.HandedToCustomer"/> entry, or the status a wrongly recorded status change goes back to.
/// </summary>
public sealed record ReplacementCorrection(
    DateOnly? ReturnedOn = null,
    string? SerialSeen = null,
    string? SerialNumber = null,
    DateOnly? HandedOverOn = null,
    ReplacementStatus? ToStatus = null)
{
    /// <summary>How many corrected values are set; a correction needs exactly one.</summary>
    public int ValueCount =>
        (ReturnedOn is null ? 0 : 1) + (SerialSeen is null ? 0 : 1) + (SerialNumber is null ? 0 : 1)
        + (HandedOverOn is null ? 0 : 1) + (ToStatus is null ? 0 : 1);
}
