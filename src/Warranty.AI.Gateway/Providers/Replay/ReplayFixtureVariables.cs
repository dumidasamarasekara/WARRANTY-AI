using System.Globalization;
using Warranty.Domain.Claims;

namespace Warranty.AI.Gateway.Providers.Replay;

/// <summary>
/// Placeholders a hand-authored or recorded fixture may contain, such as <c>{{claim.purchaseDate}}</c>.
/// Scenario claims state their purchase date relative to the day they run (a month offset), so a fixture
/// cannot hold a literal invoice date; it holds the placeholder, which replay replaces with the claim's
/// own date. Recording does the reverse, so recorded fixtures stay valid on any day.
/// </summary>
public static class ReplayFixtureVariables
{
    public const string PurchaseDate = "claim.purchaseDate";

    public const string ClaimDate = "claim.claimDate";

    public static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The placeholder values for a claim (ISO dates).</summary>
    public static IReadOnlyDictionary<string, string> For(Claim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PurchaseDate] = claim.PurchaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [ClaimDate] = claim.ClaimDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Replaces every <c>{{name}}</c> in a fixture's JSON text with its value.</summary>
    public static string Apply(string json, IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(variables);
        foreach (var (name, value) in variables)
        {
            json = json.Replace("{{" + name + "}}", value, StringComparison.Ordinal);
        }

        return json;
    }

    /// <summary>
    /// Turns recorded JSON text back into a fixture: a JSON string value equal to one of the values
    /// (e.g. <c>"2026-06-04"</c>) becomes its placeholder. The purchase date wins when two values are equal.
    /// </summary>
    public static string Templatize(string json, IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(variables);
        foreach (var (name, value) in variables.OrderBy(v => v.Key == PurchaseDate ? 0 : 1))
        {
            if (value.Length > 0)
            {
                json = json.Replace($"\"{value}\"", "\"{{" + name + "}}\"", StringComparison.Ordinal);
            }
        }

        return json;
    }
}
