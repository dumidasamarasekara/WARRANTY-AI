using System.Collections.Frozen;

namespace Warranty.Domain.Common;

/// <summary>
/// Derives a claim region from an ISO 3166-1 alpha-2 country code. The PoC covers North America
/// and the European Union; any other country cannot be mapped and leads to a request for
/// information ("region cannot be determined", spec Edge Cases).
/// </summary>
public static class RegionResolver
{
    private static readonly FrozenSet<string> NorthAmerica = new[] { "US", "CA", "MX" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> EuropeanUnion = new[]
    {
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE",
        "IT", "LV", "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool TryFromCountry(string? countryCode, out Region region)
    {
        var code = countryCode?.Trim().ToUpperInvariant();
        if (code is not null && NorthAmerica.Contains(code))
        {
            region = Region.NA;
            return true;
        }

        if (code is not null && EuropeanUnion.Contains(code))
        {
            region = Region.EU;
            return true;
        }

        region = default;
        return false;
    }
}
