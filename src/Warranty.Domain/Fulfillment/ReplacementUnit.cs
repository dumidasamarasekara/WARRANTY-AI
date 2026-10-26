using Warranty.Domain.Catalog;

namespace Warranty.Domain.Fulfillment;

/// <summary>
/// A unit reserved for the customer at a replacement location (<c>claims.replacement_units</c>), numbered
/// per replacement from 1. <see cref="ReplacementUnitResult.Reserved"/> until it is handed over, refused,
/// released by a move or cancelled; a new reservation is a new unit (and gets its own ready notification,
/// research R7). Created and changed only through <see cref="Replacement"/>.
/// </summary>
public sealed class ReplacementUnit
{
    public const int MaxResultReasonLength = 1000;

    private ReplacementUnit()
    {
        SerialNumber = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ReplacementId { get; private set; }

    /// <summary>Per replacement, from 1.</summary>
    public int Seq { get; private set; }

    /// <summary>The location holding the unit.</summary>
    public Guid ServiceCenterId { get; private set; }

    /// <summary>The claimed product, or the replacement's approved product.</summary>
    public Guid ProductId { get; private set; }

    /// <summary>Normalised with <see cref="ProductSerial.NormalizeSerial"/>; staff-only.</summary>
    public string SerialNumber { get; private set; }

    public ReplacementUnitResult Result { get; private set; }

    /// <summary>Required for <see cref="ReplacementUnitResult.Refused"/>; staff-only.</summary>
    public string? ResultReason { get; private set; }

    public DateTimeOffset ReservedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public bool IsReserved => Result == ReplacementUnitResult.Reserved;

    internal static ReplacementUnit Reserve(
        Guid tenantId,
        Guid replacementId,
        int seq,
        Guid serviceCenterId,
        Guid productId,
        string serialNumber,
        DateTimeOffset reservedAt)
    {
        if (tenantId == Guid.Empty || replacementId == Guid.Empty || serviceCenterId == Guid.Empty || productId == Guid.Empty)
        {
            throw new ArgumentException("Tenant, replacement, service-centre and product IDs are required.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seq);
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);

        return new ReplacementUnit
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ReplacementId = replacementId,
            Seq = seq,
            ServiceCenterId = serviceCenterId,
            ProductId = productId,
            SerialNumber = ProductSerial.NormalizeSerial(serialNumber),
            Result = ReplacementUnitResult.Reserved,
            ReservedAt = reservedAt,
        };
    }

    /// <summary>Ends this reserved unit with <paramref name="result"/>; a refusal needs a reason.</summary>
    internal void End(ReplacementUnitResult result, string? reason, DateTimeOffset endedAt)
    {
        if (!IsReserved)
        {
            throw new InvalidOperationException("The replacement unit has already ended.");
        }

        if (result == ReplacementUnitResult.Reserved || !Enum.IsDefined(result))
        {
            throw new ArgumentException("A unit ends as handed over, refused, released or cancelled.", nameof(result));
        }

        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (result == ReplacementUnitResult.Refused && trimmed is null)
        {
            throw new ArgumentException("A reason is required for a refused unit.", nameof(reason));
        }

        if (trimmed is { Length: > MaxResultReasonLength })
        {
            throw new ArgumentException($"A unit reason is at most {MaxResultReasonLength} characters.", nameof(reason));
        }

        Result = result;
        ResultReason = trimmed;
        EndedAt = endedAt;
    }

    /// <summary>
    /// Records the reservation of this still-reserved unit again after a correction sent its reservation back (the replacement
    /// awaited a unit with this unit kept): the unit keeps its ID — and so its one ready notification — and takes the location,
    /// model and serial now reported.
    /// </summary>
    internal void Reconfirm(Guid serviceCenterId, Guid productId, string serialNumber, DateTimeOffset reservedAt)
    {
        if (!IsReserved)
        {
            throw new InvalidOperationException("Only a reserved replacement unit can be reserved again.");
        }

        if (serviceCenterId == Guid.Empty || productId == Guid.Empty)
        {
            throw new ArgumentException("Service-centre and product IDs are required.");
        }

        ServiceCenterId = serviceCenterId;
        ProductId = productId;
        SerialNumber = ProductSerial.NormalizeSerial(serialNumber);
        ReservedAt = reservedAt;
    }

    /// <summary>Applies a corrected serial number (FR-017); the unit keeps its ID and result.</summary>
    internal void CorrectSerial(string serialNumber)
    {
        if (string.IsNullOrWhiteSpace(serialNumber))
        {
            throw new ArgumentException("The corrected serial number is required.", nameof(serialNumber));
        }

        SerialNumber = ProductSerial.NormalizeSerial(serialNumber);
    }

    /// <summary>Reserves a refused unit again after a correction sent its refusal back (FR-017).</summary>
    internal void Reinstate()
    {
        if (Result != ReplacementUnitResult.Refused)
        {
            throw new InvalidOperationException("Only a refused replacement unit can be reinstated.");
        }

        Result = ReplacementUnitResult.Reserved;
        ResultReason = null;
        EndedAt = null;
    }
}
