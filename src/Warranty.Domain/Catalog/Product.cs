namespace Warranty.Domain.Catalog;

/// <summary>A product model in a tenant's catalog (simulated ERP data).</summary>
public sealed class Product
{
    private Product()
    {
        ModelCode = Name = Category = Currency = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Unique per tenant, e.g. <c>AUR-TAB10</c>.</summary>
    public string ModelCode { get; private set; }

    public string Name { get; private set; }

    /// <summary>Lower-case category, e.g. <c>tablet</c> or <c>major-appliance</c>.</summary>
    public string Category { get; private set; }

    /// <summary>Fixed claim value compared with the tenant's auto-approval limit (clarification Q4).</summary>
    public decimal ClaimValue { get; private set; }

    public string Currency { get; private set; }

    public static Product Create(Guid id, Guid tenantId, string modelCode, string name, string category, decimal claimValue, string currency)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("Product and tenant IDs are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(modelCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(claimValue);

        return new Product
        {
            Id = id,
            TenantId = tenantId,
            ModelCode = NormalizeModelCode(modelCode),
            Name = name.Trim(),
            Category = category.Trim().ToLowerInvariant(),
            ClaimValue = decimal.Round(claimValue, 2),
            Currency = currency.Trim().ToUpperInvariant(),
        };
    }

    public static string NormalizeModelCode(string modelCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelCode);
        return modelCode.Trim().ToUpperInvariant();
    }
}
