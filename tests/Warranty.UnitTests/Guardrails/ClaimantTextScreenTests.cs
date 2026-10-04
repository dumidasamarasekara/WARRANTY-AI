using Warranty.Guardrails.Rules;

namespace Warranty.UnitTests.Guardrails;

/// <summary>Claimant-facing text screen (research R25, guardrail check <c>CLAIMANT_TEXT_SAFE</c>).</summary>
public sealed class ClaimantTextScreenTests
{
    [Theory(Skip = "Pending T120")]
    [InlineData("This claim looks like fraud.", "fraud")]
    [InlineData("FRAUD was not proven.", "FRAUD")]
    [InlineData("Several frauds were reported.", "frauds")]
    [InlineData("The invoice seems Suspicious to us.", "Suspicious")]
    [InlineData("There is a risk with this device.", "risk")]
    [InlineData("We assessed several Risks.", "Risks")]
    [InlineData("Signs of manipulation were found.", "manipulation")]
    [InlineData("Repeated manipulations of the invoice.", "manipulations")]
    [InlineData("The photo was REUSED from another submission.", "REUSED")]
    [InlineData("This is a duplicate submission.", "duplicate")]
    [InlineData("We found two Duplicates.", "Duplicates")]
    public void Disclosure_vocabulary_is_flagged_case_insensitively_including_plurals(string text, string expected)
    {
        var result = ClaimantTextScreen.Screen(text);

        result.IsSafe.ShouldBeFalse();
        result.OffendingTerm.ShouldBe(expected);
    }

    [Theory(Skip = "Pending T120")]
    [InlineData("SOURCE_INCONSISTENCY")]
    [InlineData("PRODUCT_NOT_IN_CATALOG")]
    [InlineData("SERIAL_MISMATCH_PHOTO")]
    [InlineData("DUPLICATE_SERIAL_CLAIM")]
    [InlineData("EVIDENCE_REUSED")]
    [InlineData("DAMAGE_INCONSISTENT_WITH_DESCRIPTION")]
    [InlineData("PURCHASE_DATE_ANOMALY")]
    [InlineData("MANIPULATION_ATTEMPT")]
    public void Risk_signal_codes_are_flagged(string code)
    {
        var result = ClaimantTextScreen.Screen($"Our check raised {code} on the second photo.");

        result.IsSafe.ShouldBeFalse();
        result.OffendingTerm.ShouldBe(code);
    }

    [Theory(Skip = "Pending T120")]
    [InlineData("See EV-3 for the details.", "EV-3")]
    [InlineData("This follows clause POL-12 of your terms.", "POL-12")]
    [InlineData("Rule GLB-1 applies here.", "GLB-1")]
    public void Internal_reference_ids_are_flagged(string text, string expected)
    {
        var result = ClaimantTextScreen.Screen(text);

        result.IsSafe.ShouldBeFalse();
        result.OffendingTerm.ShouldBe(expected);
    }

    [Theory(Skip = "Pending T120")]
    [InlineData("Your tablet is covered for manufacturing defects for 12 months")]
    [InlineData("accidental damage is not covered")]
    [InlineData("Your claim is approved. No other parts of the device are affected.")]
    [InlineData("Please see the asterisk note on your receipt.")]
    [InlineData("The repair is free of charge under your policy.")]
    [InlineData("")]
    public void Ordinary_outcome_text_is_safe(string text)
    {
        var result = ClaimantTextScreen.Screen(text);

        result.IsSafe.ShouldBeTrue();
        result.OffendingTerm.ShouldBeNull();
    }

    [Theory(Skip = "Pending T120")]
    [InlineData("Reused photos suggest fraud (EV-3).", "Reused")]
    [InlineData("EV-3 shows signs of fraud.", "EV-3")]
    [InlineData("We see a risk of DUPLICATE_SERIAL_CLAIM.", "risk")]
    public void The_first_offending_term_in_the_text_is_returned(string text, string expected)
    {
        var result = ClaimantTextScreen.Screen(text);

        result.IsSafe.ShouldBeFalse();
        result.OffendingTerm.ShouldBe(expected);
    }

    [Fact]
    public void Result_factories_describe_safe_and_offending_text()
    {
        ClaimantTextScreenResult.Safe.IsSafe.ShouldBeTrue();
        ClaimantTextScreenResult.Safe.OffendingTerm.ShouldBeNull();

        var offending = ClaimantTextScreenResult.Offending("fraud");
        offending.IsSafe.ShouldBeFalse();
        offending.OffendingTerm.ShouldBe("fraud");
    }
}
