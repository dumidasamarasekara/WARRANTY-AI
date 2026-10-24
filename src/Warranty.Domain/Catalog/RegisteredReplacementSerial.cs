namespace Warranty.Domain.Catalog;

/// <summary>
/// A replacement unit handed out for a claim (<c>catalog.replacement_serials</c>, 003 data-model.md): links the
/// handed-out serial to the claim's (original) serial so a later claim for the unit is recognised and its cover
/// computed from the original purchase (research R10, R11). A replacement of a replacement chains through
/// <see cref="OriginalSerial"/> and carries the original purchase date forward.
/// </summary>
public sealed class RegisteredReplacementSerial
{
    private RegisteredReplacementSerial()
    {
        SerialNumber = OriginalSerial = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>The handed-out unit's serial (<see cref="ProductSerial.NormalizeSerial"/>); unique per tenant.</summary>
    public string SerialNumber { get; private set; }

    /// <summary>Product (model) of the handed-out unit.</summary>
    public Guid ProductId { get; private set; }

    /// <summary>The claim's serial (normalised); differs from <see cref="SerialNumber"/>.</summary>
    public string OriginalSerial { get; private set; }

    /// <summary>The claim the unit was handed out for.</summary>
    public Guid ClaimId { get; private set; }

    /// <summary>The claim's purchase date, carried forward along a chain of replacements.</summary>
    public DateOnly OriginalPurchaseDate { get; private set; }

    public DateOnly HandedOverOn { get; private set; }

    /// <summary>Informational cover end at hand-over (<see cref="CoverEndOf"/>); the guardrails recompute it.</summary>
    public DateOnly CoverEndDate { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static RegisteredReplacementSerial Register(
        Guid tenantId,
        string serialNumber,
        Guid productId,
        string originalSerial,
        Guid claimId,
        DateOnly originalPurchaseDate,
        DateOnly handedOverOn,
        DateOnly coverEndDate,
        DateTimeOffset createdAt)
    {
        if (tenantId == Guid.Empty || productId == Guid.Empty || claimId == Guid.Empty)
        {
            throw new ArgumentException("Tenant, product and claim IDs are required.");
        }

        var serial = ProductSerial.NormalizeSerial(serialNumber);
        var original = ProductSerial.NormalizeSerial(originalSerial);
        if (serial == original)
        {
            throw new ArgumentException("The replacement serial must differ from the claim's serial.", nameof(serialNumber));
        }

        if (handedOverOn < originalPurchaseDate)
        {
            throw new ArgumentException("The hand-over date cannot be before the original purchase date.", nameof(handedOverOn));
        }

        return new RegisteredReplacementSerial
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SerialNumber = serial,
            ProductId = productId,
            OriginalSerial = original,
            ClaimId = claimId,
            OriginalPurchaseDate = originalPurchaseDate,
            HandedOverOn = handedOverOn,
            CoverEndDate = coverEndDate,
            CreatedAt = createdAt,
        };
    }

    /// <summary>
    /// Cover end of a replacement unit (FR-029): the later of the original coverage end and the hand-over date
    /// plus the policy's minimum cover months.
    /// </summary>
    public static DateOnly CoverEndOf(DateOnly originalCoverageEnd, DateOnly handedOverOn, int minimumCoverMonths)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumCoverMonths);
        var minimum = handedOverOn.AddMonths(minimumCoverMonths);
        return minimum > originalCoverageEnd ? minimum : originalCoverageEnd;
    }
}
