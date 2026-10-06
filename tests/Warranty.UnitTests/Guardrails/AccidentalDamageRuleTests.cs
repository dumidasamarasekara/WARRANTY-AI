using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Domain.Policies;
using Warranty.Guardrails;
using Warranty.Guardrails.Rules;

namespace Warranty.UnitTests.Guardrails;

/// <summary>
/// Accidental-damage allowance (data-model.md "Coverage window rule", T077): accidental damage is covered
/// only when <c>accidentalDamage.covered</c>, <c>claim_date ≤ purchase_date + windowMonths</c> and the
/// serial's prior approved accidental-damage claims are fewer than <c>maxIncidents</c>. The allowance is
/// coverage, not an exclusion (research R26): <c>GROUNDED_IN_CLAUSE</c> needs the rule to confirm an
/// approval of photographed accidental damage, and grounds a coverage-clause rejection only beyond the allowance.
/// </summary>
public sealed class AccidentalDamageRuleTests
{
    /// <summary>BOR-WP v1: one accidental-damage incident within the first 12 months.</summary>
    private static readonly AccidentalDamageTerms Borealis = new(true, 12, 1);

    private static readonly DateOnly Purchase = new(2025, 3, 10);

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    // ---- The rule ------------------------------------------------------------------------------------

    [Fact]
    public void The_first_incident_within_the_window_is_covered()
    {
        var result = AccidentalDamageRule.Evaluate(Borealis, Purchase, D(2026, 1, 10), priorApprovedAccidental: 0);

        result.ShouldBe(new AccidentalDamageResult(AccidentalDamageOutcome.Covered, D(2026, 3, 10), true, 0, 1));
        (result.IsCovered, result.IsBeyondAllowance).ShouldBe((true, false));
    }

    [Theory]
    [InlineData(2026, 3, 10, AccidentalDamageOutcome.Covered)]
    [InlineData(2026, 3, 11, AccidentalDamageOutcome.OutsideWindow)]
    [InlineData(2025, 3, 10, AccidentalDamageOutcome.Covered)]
    public void The_window_end_is_inclusive(int year, int month, int day, AccidentalDamageOutcome expected)
        => AccidentalDamageRule.Evaluate(Borealis, Purchase, D(year, month, day), 0).Outcome.ShouldBe(expected);

    [Fact]
    public void A_claim_after_the_window_is_beyond_the_allowance()
    {
        var result = AccidentalDamageRule.Evaluate(Borealis, Purchase, D(2026, 5, 1), 0);

        result.ShouldBe(new AccidentalDamageResult(AccidentalDamageOutcome.OutsideWindow, D(2026, 3, 10), false, 0, 1));
        (result.IsCovered, result.IsBeyondAllowance).ShouldBe((false, true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Reaching_maxIncidents_uses_up_the_allowance(int prior)
    {
        var result = AccidentalDamageRule.Evaluate(Borealis, Purchase, D(2026, 1, 10), prior);

        result.ShouldBe(new AccidentalDamageResult(AccidentalDamageOutcome.IncidentLimitReached, D(2026, 3, 10), true, prior, 1));
        (result.IsCovered, result.IsBeyondAllowance).ShouldBe((false, true));
    }

    [Fact]
    public void Prior_incidents_below_maxIncidents_still_leave_the_allowance()
    {
        var twoIncidents = new AccidentalDamageTerms(true, 24, 2);

        AccidentalDamageRule.Evaluate(twoIncidents, Purchase, D(2026, 1, 10), 1).Outcome.ShouldBe(AccidentalDamageOutcome.Covered);
        AccidentalDamageRule.Evaluate(twoIncidents, Purchase, D(2026, 1, 10), 2).Outcome.ShouldBe(AccidentalDamageOutcome.IncidentLimitReached);
    }

    [Fact]
    public void A_claim_after_the_window_is_outside_the_window_even_when_incidents_are_used_up()
        => AccidentalDamageRule.Evaluate(Borealis, Purchase, D(2026, 5, 1), 1).Outcome.ShouldBe(AccidentalDamageOutcome.OutsideWindow);

    [Fact]
    public void Terms_that_do_not_cover_accidental_damage_never_cover_it()
    {
        var result = AccidentalDamageRule.Evaluate(AccidentalDamageTerms.NotCovered, Purchase, D(2025, 4, 1), 0);

        result.ShouldBe(new AccidentalDamageResult(AccidentalDamageOutcome.NotCovered, null, null, 0, 0));
        (result.IsCovered, result.IsBeyondAllowance).ShouldBe((false, false));
    }

    [Fact]
    public void Without_an_applicable_policy_the_allowance_is_undetermined()
    {
        var result = AccidentalDamageRule.Evaluate(null, Purchase, D(2025, 4, 1), 0);

        result.Outcome.ShouldBe(AccidentalDamageOutcome.NoApplicablePolicy);
        (result.IsCovered, result.IsBeyondAllowance).ShouldBe((false, false));
    }

    [Fact]
    public void A_negative_incident_count_is_rejected()
        => Should.Throw<ArgumentOutOfRangeException>(() => AccidentalDamageRule.Evaluate(Borealis, Purchase, D(2026, 1, 10), -1));

    [Fact]
    public void The_window_end_is_purchase_plus_windowMonths_and_absent_when_not_covered()
    {
        AccidentalDamageRule.WindowEndDate(Borealis, D(2025, 1, 31)).ShouldBe(D(2026, 1, 31));
        AccidentalDamageRule.WindowEndDate(new AccidentalDamageTerms(true, 1, 1), D(2025, 1, 31)).ShouldBe(D(2025, 2, 28));
        AccidentalDamageRule.WindowEndDate(AccidentalDamageTerms.NotCovered, Purchase).ShouldBeNull();
        AccidentalDamageRule.WindowEndDate(null, Purchase).ShouldBeNull();
    }

    [Theory]
    [InlineData(true, "CRACKED_SCREEN")]
    [InlineData(true, "DENTS_OR_IMPACT")]
    [InlineData(true, "OTHER", "CRACKED_SCREEN")]
    [InlineData(false, "LIQUID_INDICATORS")]
    [InlineData(false, "COSMETIC_WEAR")]
    [InlineData(false, "NONE_VISIBLE")]
    [InlineData(false, "cracked_screen")]
    [InlineData(false)]
    public void Accidental_damage_is_evidenced_by_the_R26_damage_types(bool expected, params string[] damageTypes)
        => AccidentalDamageRule.IsEvidenced(damageTypes).ShouldBe(expected);

    // ---- GROUNDED_IN_CLAUSE through the engine ---------------------------------------------------------

    [Fact]
    public void An_approval_of_accidental_damage_within_the_allowance_is_grounded()
    {
        var outcome = GuardrailScenario.ApproveAccidentalDamage().Evaluate();

        var check = Check(outcome);
        check.Passed.ShouldBeTrue(check.Actual);
        check.Actual.ShouldNotBeNull().ShouldContain("EV-2 shows accidental damage (CRACKED_SCREEN): within the accidental-damage allowance");
        outcome.Disposition.ShouldBe(Disposition.AutoApprove);
    }

    [Fact]
    public void An_approval_of_accidental_damage_after_the_allowance_window_goes_to_review()
    {
        var scenario = GuardrailScenario.ApproveAccidentalDamage();
        scenario.PurchaseDate = new DateOnly(2025, 6, 1); // 15 months before the claim date; the defect window has not ended

        var outcome = scenario.Evaluate();

        var check = Check(outcome);
        check.Passed.ShouldBeFalse();
        check.Actual.ShouldNotBeNull().ShouldContain("the accidental-damage allowance ended on 2026-06-01");
        outcome.Checks.ShouldContain(c => c.Code == GuardrailCheckCode.CoverageWindowAgrees && c.Passed);
        outcome.Disposition.ShouldBe(Disposition.HumanReview);
    }

    [Fact]
    public void An_approval_of_a_second_accidental_incident_goes_to_review()
    {
        var scenario = GuardrailScenario.ApproveAccidentalDamage();
        scenario.PriorApprovedAccidental = 1;

        var outcome = scenario.Evaluate();

        var check = Check(outcome);
        check.Passed.ShouldBeFalse();
        check.Actual.ShouldNotBeNull().ShouldContain("allowance is used up (1 of 1 incident(s) already approved)");
        outcome.Disposition.ShouldBe(Disposition.HumanReview);
    }

    [Fact]
    public void An_approval_of_accidental_damage_under_a_policy_that_excludes_it_goes_to_review()
    {
        var scenario = GuardrailScenario.ClearApprove();
        scenario.PhotoDamageTypes.Clear();
        scenario.PhotoDamageTypes.Add("DENTS_OR_IMPACT");

        var outcome = scenario.Evaluate();

        var check = Check(outcome);
        check.Passed.ShouldBeFalse();
        check.Actual.ShouldNotBeNull().ShouldContain("the applicable version does not cover accidental damage");
        outcome.Disposition.ShouldBe(Disposition.HumanReview);
    }

    [Fact]
    public void An_approval_without_photographed_accidental_damage_ignores_the_allowance()
    {
        var scenario = GuardrailScenario.ApproveAccidentalDamage();
        scenario.PhotoDamageTypes.Clear();
        scenario.PhotoDamageTypes.Add("NONE_VISIBLE");
        scenario.PriorApprovedAccidental = 5;

        var outcome = scenario.Evaluate();

        Check(outcome).Passed.ShouldBeTrue();
        outcome.Disposition.ShouldBe(Disposition.AutoApprove);
    }

    [Theory]
    [InlineData(2025, 6, 1, 0)] // after the 12-month allowance
    [InlineData(2025, 11, 1, 1)] // the one incident was already approved
    public void A_rejection_on_the_coverage_clause_is_grounded_when_the_rule_confirms_the_allowance_is_exceeded(
        int purchaseYear, int purchaseMonth, int purchaseDay, int prior)
    {
        var scenario = RejectOnAllowance();
        scenario.PurchaseDate = new DateOnly(purchaseYear, purchaseMonth, purchaseDay);
        scenario.PriorApprovedAccidental = prior;

        var outcome = scenario.Evaluate();

        var check = Check(outcome);
        check.Passed.ShouldBeTrue(check.Actual);
        check.Actual.ShouldNotBeNull().ShouldContain("POL-1: Coverage clause, EV-2 shows accidental damage (CRACKED_SCREEN) and the accidental-damage allowance");
        outcome.Disposition.ShouldBe(Disposition.AutoReject);
    }

    [Fact]
    public void A_rejection_on_the_coverage_clause_within_the_allowance_is_not_grounded()
    {
        var outcome = RejectOnAllowance().Evaluate();

        var check = Check(outcome);
        check.Passed.ShouldBeFalse();
        check.Actual.ShouldNotBeNull().ShouldContain("but the rejection is not confirmed: within the accidental-damage allowance");
        outcome.Disposition.ShouldBe(Disposition.HumanReview);
    }

    [Fact]
    public void A_rejection_on_a_coverage_clause_without_photographed_accidental_damage_is_not_grounded()
    {
        var scenario = RejectOnAllowance();
        scenario.PurchaseDate = new DateOnly(2025, 6, 1);
        scenario.PhotoDamageTypes.Clear();
        scenario.PhotoDamageTypes.Add("NONE_VISIBLE");

        var outcome = scenario.Evaluate();

        var check = Check(outcome);
        check.Passed.ShouldBeFalse();
        check.Actual.ShouldNotBeNull().ShouldContain("POL-1: a Coverage clause cannot ground a rejection");
        outcome.Disposition.ShouldBe(Disposition.HumanReview);
    }

    /// <summary>Tenant B: the AI rejects a cracked screen on the accidental-damage coverage clause, inside the defect window.</summary>
    private static GuardrailScenario RejectOnAllowance()
    {
        var scenario = GuardrailScenario.ApproveAccidentalDamage();
        scenario.Decision = AiDecision.Reject;
        scenario.Coverage = CoverageDetermination.NotCovered;
        scenario.ClaimantExplanation = "Your plan's accidental-damage repair does not apply to this claim, so this repair is not covered.";
        scenario.PolicyCitations.Clear();
        scenario.PolicyCitations.Add(new PolicyCitation(GuardrailScenario.CoverageClause, PolicyRefRelevance.SupportsRejection));
        return scenario;
    }

    private static GuardrailCheck Check(GuardrailOutcome outcome)
        => outcome.Checks.Single(check => check.Code == GuardrailCheckCode.GroundedInClause);
}
