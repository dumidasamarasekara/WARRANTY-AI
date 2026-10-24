namespace Warranty.Domain.Fulfillment;

/// <summary>
/// The corrected value of a <see cref="RepairEventType.Correction"/> (002 contracts <c>RepairEventRequest</c>, FR-027):
/// exactly one of the hand-over date or centre reference of a <see cref="RepairEventType.HandedOver"/> entry, the amount of
/// an <see cref="RepairEventType.EstimateReported"/> entry, or the status a wrongly recorded status change goes back to.
/// </summary>
public sealed record RepairCorrection(
    DateOnly? HandedOverOn = null,
    string? CentreReference = null,
    decimal? EstimateAmount = null,
    RepairStatus? ToStatus = null)
{
    /// <summary>How many corrected values are set; a correction needs exactly one.</summary>
    public int ValueCount =>
        (HandedOverOn is null ? 0 : 1) + (CentreReference is null ? 0 : 1) + (EstimateAmount is null ? 0 : 1) + (ToStatus is null ? 0 : 1);
}
