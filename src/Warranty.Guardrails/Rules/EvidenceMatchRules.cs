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
    /// <summary>Serial number or model code in comparison form: upper-case, without spaces, <c>-</c>, <c>_</c> and <c>.</c>.</summary>
    public static string NormalizeIdentifier(string identifier) =>
        throw new NotImplementedException("Pending T122");

    /// <summary>Serial numbers or model codes match when their <see cref="NormalizeIdentifier"/> forms are equal.</summary>
    public static EvidenceMatch IdentifiersMatch(string? claimed, string? evidence) =>
        throw new NotImplementedException("Pending T122");

    /// <summary>Purchase dates match only on the exact calendar date.</summary>
    public static EvidenceMatch DatesMatch(DateOnly? claimed, DateOnly? evidence) =>
        throw new NotImplementedException("Pending T122");

    /// <summary>Prices match when <c>|invoice − claim| ≤ max(1% of claim, 1.00)</c> (claim currency).</summary>
    public static EvidenceMatch PriceMatches(decimal? claim, decimal? invoice) =>
        throw new NotImplementedException("Pending T122");

    /// <summary>
    /// Seller name in comparison form: lower-case, punctuation replaced by spaces, spaces collapsed and
    /// trimmed, trailing legal suffixes dropped (<c>inc</c>, <c>incorporated</c>, <c>ltd</c>, <c>limited</c>,
    /// <c>llc</c>, <c>gmbh</c>, <c>ag</c>, <c>sa</c>, <c>bv</c>, <c>plc</c>, <c>co</c>, <c>corp</c>,
    /// <c>corporation</c>). E.g. <c>"AURORA STORE, Inc."</c> and <c>"Aurora-Store Ltd."</c> → <c>"aurora store"</c>.
    /// </summary>
    public static string NormalizeSeller(string seller) =>
        throw new NotImplementedException("Pending T122");

    /// <summary>Seller names match when their <see cref="NormalizeSeller"/> forms are equal.</summary>
    public static EvidenceMatch SellersMatch(string? claimed, string? evidence) =>
        throw new NotImplementedException("Pending T122");
}
