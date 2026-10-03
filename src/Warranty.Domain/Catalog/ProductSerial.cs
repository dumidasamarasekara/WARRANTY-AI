namespace Warranty.Domain.Catalog;

/// <summary>A serial number registered to a product in a tenant's catalog (key: tenant + serial).</summary>
public sealed class ProductSerial
{
    private ProductSerial()
    {
        SerialNumber = string.Empty;
    }

    public Guid TenantId { get; private set; }

    /// <summary>Upper-case, trimmed serial number.</summary>
    public string SerialNumber { get; private set; }

    public Guid ProductId { get; private set; }

    public DateOnly? ManufacturedOn { get; private set; }

    public static ProductSerial Create(Guid tenantId, string serialNumber, Guid productId, DateOnly? manufacturedOn = null)
    {
        if (tenantId == Guid.Empty || productId == Guid.Empty)
        {
            throw new ArgumentException("Tenant and product IDs are required.");
        }

        return new ProductSerial
        {
            TenantId = tenantId,
            SerialNumber = NormalizeSerial(serialNumber),
            ProductId = productId,
            ManufacturedOn = manufacturedOn,
        };
    }

    /// <summary>Storage form of a serial number (upper-case, trimmed). Evidence matching is looser (research R27).</summary>
    public static string NormalizeSerial(string serialNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        return serialNumber.Trim().ToUpperInvariant();
    }
}
