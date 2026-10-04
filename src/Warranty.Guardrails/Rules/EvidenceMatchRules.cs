using System.Text;

namespace Warranty.Guardrails.Rules;

/// <summary>Result of comparing one claim field with the same field in the evidence (research R27).</summary>
public enum EvidenceMatch
{
    /// <summary>A value is missing (<see langword="null"/>, e.g. the invoice does not show it): not a conflict.</summary>
    NotCompared,

    /// <summary>The values agree under the FR-016 tolerance.</summary>
    Match,

    /// <summary>The values differ beyond the FR-016 tolerance: conflicting evidence.</summary>
    Mismatch,
}

/// <summary>
/// Evidence matching tolerances (FR-016, research R27), shared by the <c>invoice_validation</c> tool,
/// the serial-in-photo comparison and the guardrail conflict checks. Pure; no AI reference.
/// Every <c>*Match</c>/<c>*Matches</c> method returns <see cref="EvidenceMatch.NotCompared"/> when either
/// value is <see langword="null"/>.
/// </summary>
public static class EvidenceMatchRules
{
    private const decimal PriceTolerancePercent = 0.01m;
    private const decimal PriceToleranceFloor = 1.00m;

    private static readonly HashSet<string> LegalSuffixes = new(StringComparer.Ordinal)
    {
        "inc", "incorporated", "ltd", "limited", "llc", "gmbh", "ag", "sa", "bv", "plc", "co", "corp", "corporation",
    };

    /// <summary>Longest legal suffix, in letters; bounds the dotted-suffix look-back (<c>"g m b h"</c>).</summary>
    private static readonly int LongestSuffixLength = LegalSuffixes.Max(s => s.Length);

    /// <summary>Serial number or model code in comparison form: upper-case, without spaces, <c>-</c>, <c>_</c> and <c>.</c>.</summary>
    public static string NormalizeIdentifier(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        var builder = new StringBuilder(identifier.Length);
        foreach (var c in identifier)
        {
            if (char.IsWhiteSpace(c) || c is '-' or '_' or '.')
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>Serial numbers or model codes match when their <see cref="NormalizeIdentifier"/> forms are equal.</summary>
    public static EvidenceMatch IdentifiersMatch(string? claimed, string? evidence) =>
        claimed is null || evidence is null
            ? EvidenceMatch.NotCompared
            : ToMatch(string.Equals(NormalizeIdentifier(claimed), NormalizeIdentifier(evidence), StringComparison.Ordinal));

    /// <summary>Purchase dates match only on the exact calendar date.</summary>
    public static EvidenceMatch DatesMatch(DateOnly? claimed, DateOnly? evidence) =>
        claimed is null || evidence is null
            ? EvidenceMatch.NotCompared
            : ToMatch(claimed.Value == evidence.Value);

    /// <summary>Prices match when <c>|invoice − claim| ≤ max(1% of claim, 1.00)</c> (claim currency).</summary>
    public static EvidenceMatch PriceMatches(decimal? claim, decimal? invoice)
    {
        if (claim is null || invoice is null)
        {
            return EvidenceMatch.NotCompared;
        }

        var tolerance = Math.Max(claim.Value * PriceTolerancePercent, PriceToleranceFloor);
        return ToMatch(Math.Abs(invoice.Value - claim.Value) <= tolerance);
    }

    /// <summary>
    /// Seller name in comparison form: lower-case, punctuation replaced by spaces, spaces collapsed and
    /// trimmed, trailing legal suffixes dropped (<c>inc</c>, <c>incorporated</c>, <c>ltd</c>, <c>limited</c>,
    /// <c>llc</c>, <c>gmbh</c>, <c>ag</c>, <c>sa</c>, <c>bv</c>, <c>plc</c>, <c>co</c>, <c>corp</c>,
    /// <c>corporation</c>). E.g. <c>"AURORA STORE, Inc."</c> and <c>"Aurora-Store Ltd."</c> → <c>"aurora store"</c>.
    /// </summary>
    /// <remarks>
    /// Suffixes are dropped repeatedly from the end (<c>"Aurora Store Co. Ltd."</c> → <c>"aurora store"</c>),
    /// but the first word is never dropped, so a name made only of a suffix word stays comparable
    /// (<c>"Co."</c> → <c>"co"</c>). A suffix written with dots between its letters (<c>"S.A."</c>,
    /// <c>"B.V."</c>, <c>"G.m.b.H."</c>) becomes single-letter words; a trailing run of single letters that
    /// spells a legal suffix is dropped as that suffix. A suffix word inside the name is kept
    /// (<c>"Aurora Co Store"</c> stays <c>"aurora co store"</c>).
    /// </remarks>
    public static string NormalizeSeller(string seller)
    {
        ArgumentNullException.ThrowIfNull(seller);

        var builder = new StringBuilder(seller.Length);
        foreach (var c in seller)
        {
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        var words = builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        while (words.Count > 1)
        {
            var suffixWords = TrailingSuffixWordCount(words);
            if (suffixWords == 0)
            {
                break;
            }

            words.RemoveRange(words.Count - suffixWords, suffixWords);
        }

        return string.Join(' ', words);
    }

    /// <summary>Seller names match when their <see cref="NormalizeSeller"/> forms are equal.</summary>
    public static EvidenceMatch SellersMatch(string? claimed, string? evidence) =>
        claimed is null || evidence is null
            ? EvidenceMatch.NotCompared
            : ToMatch(string.Equals(NormalizeSeller(claimed), NormalizeSeller(evidence), StringComparison.Ordinal));

    /// <summary>
    /// Number of trailing words that form one legal suffix (a suffix word, or single letters spelling one),
    /// leaving at least one word; 0 when the name does not end in a legal suffix.
    /// </summary>
    private static int TrailingSuffixWordCount(List<string> words)
    {
        if (LegalSuffixes.Contains(words[^1]))
        {
            return 1;
        }

        // Dotted suffix: "s a" → "sa", "g m b h" → "gmbh". Shortest spelling first.
        var maxLetters = Math.Min(LongestSuffixLength, words.Count - 1);
        var spelled = string.Empty;
        for (var count = 1; count <= maxLetters; count++)
        {
            var word = words[^count];
            if (word.Length != 1)
            {
                break;
            }

            spelled = word + spelled;
            if (count > 1 && LegalSuffixes.Contains(spelled))
            {
                return count;
            }
        }

        return 0;
    }

    private static EvidenceMatch ToMatch(bool matches) => matches ? EvidenceMatch.Match : EvidenceMatch.Mismatch;
}
