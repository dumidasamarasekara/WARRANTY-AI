namespace Warranty.Domain.Policies;

/// <summary>A tenant's warranty terms document; its content lives in versions and clauses.</summary>
public sealed class WarrantyPolicy
{
    private WarrantyPolicy()
    {
        Code = Title = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Unique per tenant, e.g. <c>AUR-WP</c>.</summary>
    public string Code { get; private set; }

    public string Title { get; private set; }

    public static WarrantyPolicy Create(Guid id, Guid tenantId, string code, string title)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("Policy and tenant IDs are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        return new WarrantyPolicy { Id = id, TenantId = tenantId, Code = code.Trim().ToUpperInvariant(), Title = title.Trim() };
    }
}
