using Warranty.Domain.Common;

namespace Warranty.Domain.Crm;

/// <summary>
/// A tenant's customer (simulated CRM). Name, email, phone and street address are personal data:
/// they are never placed in AI prompts (FR-006a) and appear there only as placeholders.
/// </summary>
public sealed class Customer
{
    private Customer()
    {
        FullName = Email = Country = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string FullName { get; private set; }

    /// <summary>Lower-case email; customers are matched by (tenant, email).</summary>
    public string Email { get; private set; }

    /// <summary>E.164-style phone number, when given.</summary>
    public string? Phone { get; private set; }

    public string? AddressLine { get; private set; }

    public string? City { get; private set; }

    public string? PostalCode { get; private set; }

    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    public string Country { get; private set; }

    /// <summary>Region derived from the country; null when the country is outside NA/EU.</summary>
    public Region? Region { get; private set; }

    public static Customer Create(
        Guid id,
        Guid tenantId,
        string fullName,
        string email,
        string country,
        string? phone = null,
        string? addressLine = null,
        string? city = null,
        string? postalCode = null)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("Customer and tenant IDs are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(country);
        var countryCode = country.Trim().ToUpperInvariant();
        if (countryCode.Length != 2 || !countryCode.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException($"Country '{country}' must be an ISO 3166-1 alpha-2 code.", nameof(country));
        }

        return new Customer
        {
            Id = id,
            TenantId = tenantId,
            FullName = fullName.Trim(),
            Email = ContactNormalizer.NormalizeEmail(email),
            Phone = string.IsNullOrWhiteSpace(phone) ? null : ContactNormalizer.NormalizePhone(phone),
            AddressLine = Blank(addressLine),
            City = Blank(city),
            PostalCode = Blank(postalCode),
            Country = countryCode,
            Region = RegionResolver.TryFromCountry(countryCode, out var region) ? region : null,
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
