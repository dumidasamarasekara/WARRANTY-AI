using Warranty.Guardrails.Rules;

namespace Warranty.UnitTests.Guardrails;

/// <summary>Evidence matching tolerances (FR-016, research R27).</summary>
public sealed class EvidenceMatchRulesTests
{
    // ---- Serial number and model code ----

    [Theory]
    [InlineData(" sn-48_21.33 77 ", "SN48213377")]
    [InlineData("tab-x10", "TABX10")]
    [InlineData("AB12", "AB12")]
    public void NormalizeIdentifier_upper_cases_and_removes_spaces_dashes_underscores_and_dots(string identifier, string expected) =>
        EvidenceMatchRules.NormalizeIdentifier(identifier).ShouldBe(expected);

    [Theory]
    [InlineData("SN-4821-3377", "sn 4821 3377")]
    [InlineData("SN_48213377", "sn.4821.3377")]
    [InlineData("TAB-X10", "tabx10")]
    [InlineData("AB12", "AB12")]
    public void Identifiers_match_ignoring_case_spaces_dashes_underscores_and_dots(string claimed, string evidence) =>
        EvidenceMatchRules.IdentifiersMatch(claimed, evidence).ShouldBe(EvidenceMatch.Match);

    [Theory]
    [InlineData("SN-48213377", "SN-48213378")]
    [InlineData("SN48213377", "SN482133770")]
    [InlineData("SN48213377", "SN/48213377")]
    [InlineData("SN48213377", "SN:48213377")]
    [InlineData("TAB-X10", "TAB-X1O")]
    public void Identifiers_mismatch_on_any_other_difference(string claimed, string evidence) =>
        EvidenceMatchRules.IdentifiersMatch(claimed, evidence).ShouldBe(EvidenceMatch.Mismatch);

    // ---- Purchase date ----

    [Fact]
    public void Dates_match_only_on_the_exact_calendar_date()
    {
        var purchased = new DateOnly(2026, 3, 14);

        EvidenceMatchRules.DatesMatch(purchased, new DateOnly(2026, 3, 14)).ShouldBe(EvidenceMatch.Match);
        EvidenceMatchRules.DatesMatch(purchased, new DateOnly(2026, 3, 15)).ShouldBe(EvidenceMatch.Mismatch);
        EvidenceMatchRules.DatesMatch(purchased, new DateOnly(2026, 3, 13)).ShouldBe(EvidenceMatch.Mismatch);
        EvidenceMatchRules.DatesMatch(purchased, new DateOnly(2025, 3, 14)).ShouldBe(EvidenceMatch.Mismatch);
    }

    // ---- Price: |invoice − claim| ≤ max(1% of claim, 1.00) ----

    public static TheoryData<decimal, decimal, EvidenceMatch> Prices => new()
    {
        // 1% of 450.00 = 4.50 is the tolerance.
        { 450.00m, 450.00m, EvidenceMatch.Match },
        { 450.00m, 449.50m, EvidenceMatch.Match },
        { 450.00m, 454.50m, EvidenceMatch.Match },
        { 450.00m, 454.51m, EvidenceMatch.Mismatch },
        { 450.00m, 445.50m, EvidenceMatch.Match },
        { 450.00m, 445.49m, EvidenceMatch.Mismatch },
        // 1% of 50.00 = 0.50, so the 1.00 floor applies.
        { 50.00m, 51.00m, EvidenceMatch.Match },
        { 50.00m, 51.01m, EvidenceMatch.Mismatch },
        { 50.00m, 49.00m, EvidenceMatch.Match },
        { 50.00m, 48.99m, EvidenceMatch.Mismatch },
    };

    [Theory]
    [MemberData(nameof(Prices))]
    public void Prices_match_within_one_percent_or_one_unit_whichever_is_larger(decimal claim, decimal invoice, EvidenceMatch expected) =>
        EvidenceMatchRules.PriceMatches(claim, invoice).ShouldBe(expected);

    // ---- Seller ----

    [Theory]
    [InlineData("AURORA STORE, Inc.", "aurora store")]
    [InlineData("Aurora-Store Ltd.", "aurora store")]
    [InlineData("  Nordic   Electronics GmbH ", "nordic electronics")]
    [InlineData("Aurora Store", "aurora store")]
    // Repeated trailing suffixes are all dropped; the first word never is.
    [InlineData("Aurora Store Co. Ltd.", "aurora store")]
    [InlineData("Co.", "co")]
    // Dotted suffixes: a trailing run of single letters spelling a suffix is dropped.
    [InlineData("Aurora Store S.A.", "aurora store")]
    [InlineData("Aurora Store B.V.", "aurora store")]
    [InlineData("Nordic Electronics G.m.b.H.", "nordic electronics")]
    [InlineData("Aurora Store X", "aurora store x")]
    public void NormalizeSeller_lower_cases_strips_punctuation_collapses_spaces_and_drops_legal_suffixes(string seller, string expected) =>
        EvidenceMatchRules.NormalizeSeller(seller).ShouldBe(expected);

    [Theory]
    [InlineData("AURORA STORE, Inc.")]
    [InlineData("Aurora-Store Ltd.")]
    [InlineData("aurora store")]
    [InlineData("Aurora  Store Incorporated")]
    [InlineData("Aurora Store Limited")]
    [InlineData("Aurora Store LLC")]
    [InlineData("Aurora Store GmbH")]
    [InlineData("Aurora Store AG")]
    [InlineData("Aurora Store SA")]
    [InlineData("Aurora Store BV")]
    [InlineData("Aurora Store plc")]
    [InlineData("Aurora Store Co.")]
    [InlineData("Aurora Store Corp.")]
    [InlineData("Aurora Store Corporation")]
    [InlineData("Aurora Store S.A.")]
    [InlineData("Aurora Store B.V.")]
    public void Sellers_match_ignoring_case_punctuation_and_legal_suffixes(string invoiceSeller) =>
        EvidenceMatchRules.SellersMatch("Aurora Store", invoiceSeller).ShouldBe(EvidenceMatch.Match);

    [Theory]
    [InlineData("Aurora Stores")]
    [InlineData("Borealis Store")]
    [InlineData("Aurora Co Store")]
    [InlineData("Aurora S.A. Store")]
    public void Sellers_mismatch_on_other_differences(string invoiceSeller) =>
        EvidenceMatchRules.SellersMatch("Aurora Store", invoiceSeller).ShouldBe(EvidenceMatch.Mismatch);

    // ---- Missing evidence ----

    [Fact]
    public void A_null_invoice_field_is_not_compared_and_never_a_mismatch()
    {
        EvidenceMatchRules.IdentifiersMatch("SN-48213377", null).ShouldBe(EvidenceMatch.NotCompared);
        EvidenceMatchRules.DatesMatch(new DateOnly(2026, 3, 14), null).ShouldBe(EvidenceMatch.NotCompared);
        EvidenceMatchRules.PriceMatches(450.00m, null).ShouldBe(EvidenceMatch.NotCompared);
        EvidenceMatchRules.SellersMatch("Aurora Store", null).ShouldBe(EvidenceMatch.NotCompared);
    }
}
