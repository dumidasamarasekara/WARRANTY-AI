using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Guardrails;

namespace Warranty.UnitTests.Guardrails;

/// <summary>
/// The protection-plan escalation check (002 FR-006, FR-037, research R9): a claim goes to human review with
/// the reason <c>PROTECTION_PLAN_TO_CHECK</c> only when the claimant states an additional protection plan, the
/// damage is accidental (an <c>ACCIDENTAL_DAMAGE</c> exclusion of the version, or accidental damage beyond the
/// version's allowance) and the applicable version's <c>remedies.protectionPlan.liftsAccidentalDamageExclusion</c>
/// is true. In every other case the plan changes nothing: the 001 outcome stands (SC-009).
/// </summary>
public sealed class ProtectionPlanCheckTests
{
    /// <summary>Wire name of the check (data-model.md order: recorded last, after <c>CLAIMANT_TEXT_SAFE</c>).</summary>
    private const string CheckCode = "PROTECTION_PLAN_NOT_RELIED_ON";

    /// <summary>Borealis BOR-WP v1: a confirmed plan lifts the accidental-damage exclusion; the remedy is then Repair.</summary>
    private static readonly RemedyRules PlanLiftsExclusion = new(
        new RemedyChoice(RemedyType.Repair, null), null, null, new ProtectionPlanRule(true, RemedyType.Repair, null));

    /// <summary>Aurora AUR-WP v1/v2: a plan does not lift the exclusion.</summary>
    private static readonly RemedyRules PlanDoesNotLift = new(
        new RemedyChoice(RemedyType.Repair, null), null, null, new ProtectionPlanRule(false, null, null));

    /// <summary>Remedy rules without a protection-plan row.</summary>
    private static readonly RemedyRules NoPlanRow = new(new RemedyChoice(RemedyType.Repair, null), null, null, null);

    // ── Escalation: plan stated, accidental damage, the version lets a plan lift the exclusion ──────────────────

    [Fact(Skip = "Pending T261")]
    public void Accidental_damage_after_the_included_incident_with_a_plan_goes_to_review_with_the_plan_reason()
    {
        var scenario = BeyondAllowanceByIncident(PlanLiftsExclusion);
        scenario.HasProtectionPlan = true;

        var outcome = scenario.Evaluate();

        ShouldEscalateForThePlan(outcome);
        PlanCheck(outcome).Actual.ShouldNotBeNull().ShouldContain("allowance is used up");
    }

    [Fact(Skip = "Pending T261")]
    public void Accidental_damage_after_the_allowance_window_with_a_plan_goes_to_review_with_the_plan_reason()
    {
        var scenario = GuardrailScenario.ApproveAccidentalDamage(PlanLiftsExclusion);
        scenario.PurchaseDate = new DateOnly(2025, 6, 1); // 15 months before the claim date; the 12-month allowance has ended
        scenario.HasProtectionPlan = true;

        var outcome = scenario.Evaluate();

        ShouldEscalateForThePlan(outcome);
        PlanCheck(outcome).Actual.ShouldNotBeNull().ShouldContain("allowance ended");
    }

    [Fact(Skip = "Pending T261")]
    public void An_evidenced_accidental_damage_exclusion_that_a_plan_lifts_goes_to_review_instead_of_an_automatic_rejection()
    {
        var withoutPlan = GuardrailScenario.RejectOnExclusion(PlanLiftsExclusion, GuardrailScenario.AccidentalExclusion, "CRACKED_SCREEN");
        withoutPlan.Version.Terms.Excludes(ExclusionCode.AccidentalDamage).ShouldBeTrue("precondition");
        withoutPlan.Evaluate().Disposition.ShouldBe(Disposition.AutoReject, "precondition: without a plan the exclusion rejects automatically");

        var scenario = GuardrailScenario.RejectOnExclusion(PlanLiftsExclusion, GuardrailScenario.AccidentalExclusion, "CRACKED_SCREEN");
        scenario.HasProtectionPlan = true;

        ShouldEscalateForThePlan(scenario.Evaluate());
    }

    [Fact(Skip = "Pending T261")]
    public void A_cited_accidental_damage_exclusion_counts_as_accidental_damage_even_without_a_photo_showing_it()
    {
        var scenario = GuardrailScenario.RejectOnExclusion(PlanLiftsExclusion, GuardrailScenario.AccidentalExclusion, "NONE_VISIBLE");
        scenario.HasProtectionPlan = true;

        ShouldEscalateForThePlan(scenario.Evaluate());
    }

    [Fact(Skip = "Pending T261")]
    public void The_plan_reason_is_added_to_the_other_escalation_reasons()
    {
        var scenario = BeyondAllowanceByIncident(PlanLiftsExclusion);
        scenario.HasProtectionPlan = true;
        scenario.ClaimValue = 5_000m;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        outcome.Reasons.ShouldContain(EscalationReason.ProtectionPlanToCheck);
        outcome.Reasons.ShouldContain(EscalationReason.ValueAboveLimit);
    }

    // ── The check is recorded on every evaluation ────────────────────────────────────────────────────────────

    [Fact(Skip = "Pending T261")]
    public void The_check_is_recorded_last_and_passes_when_no_plan_is_stated()
    {
        var outcome = GuardrailScenario.ClearApprove(PlanLiftsExclusion).Evaluate();

        outcome.Checks[^1].ShouldBe(PlanCheck(outcome));
        PlanCheck(outcome).Passed.ShouldBeTrue();
        outcome.Disposition.ShouldBe(Disposition.AutoApprove);
    }

    [Fact(Skip = "Pending T261")]
    public void The_check_passes_and_says_why_when_the_version_does_not_let_a_plan_lift_the_exclusion()
    {
        var scenario = GuardrailScenario.RejectOnExclusion(PlanDoesNotLift, GuardrailScenario.AccidentalExclusion, "CRACKED_SCREEN");
        scenario.HasProtectionPlan = true;

        var check = PlanCheck(scenario.Evaluate());

        check.Passed.ShouldBeTrue();
        check.Message.ShouldNotBeNull().ShouldContain("does not let a protection plan lift");
    }

    [Fact(Skip = "Pending T261")]
    public void The_check_does_not_apply_when_intake_short_circuits()
    {
        var scenario = BeyondAllowanceByIncident(PlanLiftsExclusion);
        scenario.HasProtectionPlan = true;
        scenario.AiAvailable = false;
        scenario.IntakeMissingItems.Add(RequestedItem.Create(RequestedItemCodes.ProtectionPlanDocument, "Please add your protection plan document."));

        var check = PlanCheck(scenario.Evaluate());

        check.Passed.ShouldBeTrue();
        check.Actual.ShouldBe("not applicable");
    }

    // ── No effect: the 001 outcome stands (FR-006, SC-009) ───────────────────────────────────────────────────

    [Fact]
    public void Without_a_stated_plan_accidental_damage_beyond_the_allowance_keeps_its_001_outcome()
    {
        var outcome = BeyondAllowanceByIncident(PlanLiftsExclusion).Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        outcome.Reasons.ShouldNotContain(EscalationReason.ProtectionPlanToCheck);
    }

    [Fact]
    public void Without_a_stated_plan_an_excluded_accidental_damage_is_still_rejected_automatically()
    {
        var outcome = GuardrailScenario.RejectOnExclusion(PlanLiftsExclusion, GuardrailScenario.AccidentalExclusion, "CRACKED_SCREEN").Evaluate();

        outcome.Disposition.ShouldBe(Disposition.AutoReject);
        outcome.Reasons.ShouldBeEmpty();
    }

    [Fact]
    public void A_plan_changes_nothing_when_the_version_does_not_let_it_lift_the_exclusion()
    {
        var scenario = GuardrailScenario.RejectOnExclusion(PlanDoesNotLift, GuardrailScenario.AccidentalExclusion, "CRACKED_SCREEN");
        scenario.HasProtectionPlan = true;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.AutoReject);
        outcome.Reasons.ShouldBeEmpty();
    }

    [Fact]
    public void A_plan_changes_nothing_when_the_version_has_no_protection_plan_row()
    {
        var scenario = GuardrailScenario.RejectOnExclusion(NoPlanRow, GuardrailScenario.AccidentalExclusion, "CRACKED_SCREEN");
        scenario.HasProtectionPlan = true;

        scenario.Evaluate().Disposition.ShouldBe(Disposition.AutoReject);
    }

    [Fact]
    public void A_plan_changes_nothing_when_the_version_has_no_remedy_rules()
    {
        var scenario = GuardrailScenario.RejectOnExclusion(GuardrailScenario.AccidentalExclusion, "CRACKED_SCREEN");
        scenario.HasProtectionPlan = true;

        scenario.Evaluate().Disposition.ShouldBe(Disposition.AutoReject);
    }

    [Fact]
    public void A_plan_changes_nothing_when_the_allowance_covers_the_accidental_damage()
    {
        var scenario = GuardrailScenario.ApproveAccidentalDamage(PlanLiftsExclusion);
        scenario.HasProtectionPlan = true;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.AutoApprove);
        outcome.Reasons.ShouldBeEmpty();
    }

    [Fact]
    public void A_plan_changes_nothing_on_a_defect_claim_without_accidental_damage()
    {
        var scenario = GuardrailScenario.ClearApprove(PlanLiftsExclusion);
        scenario.HasProtectionPlan = true;
        scenario.PriorApprovedAccidental = 1;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.AutoApprove);
        outcome.Reasons.ShouldBeEmpty();
    }

    [Fact]
    public void A_plan_changes_nothing_on_a_rejection_for_an_expired_warranty_period()
    {
        var scenario = GuardrailScenario.ClearReject(PlanLiftsExclusion);
        scenario.HasProtectionPlan = true;

        scenario.Evaluate().Disposition.ShouldBe(Disposition.AutoReject);
    }

    [Fact]
    public void A_stated_plan_without_its_document_leads_to_a_request_for_information_not_to_review()
    {
        var scenario = BeyondAllowanceByIncident(PlanLiftsExclusion);
        scenario.HasProtectionPlan = true;
        scenario.AiAvailable = false;
        scenario.IntakeMissingItems.Add(RequestedItem.Create(RequestedItemCodes.ProtectionPlanDocument, "Please add your protection plan document."));

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.RequestInformation);
        outcome.Action.ShouldNotBeNull().RequestedItems.ShouldHaveSingleItem().Item.ShouldBe(RequestedItemCodes.ProtectionPlanDocument);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Borealis-like allowance (one incident in 12 months) that the serial already used; the AI approves a cracked screen.</summary>
    private static GuardrailScenario BeyondAllowanceByIncident(RemedyRules remedyRules)
    {
        var scenario = GuardrailScenario.ApproveAccidentalDamage(remedyRules);
        scenario.PriorApprovedAccidental = 1;
        return scenario;
    }

    private static GuardrailCheck PlanCheck(GuardrailOutcome outcome)
        => outcome.Checks.SingleOrDefault(c => WireName.Of(c.Code) == CheckCode).ShouldNotBeNull($"{CheckCode} is not recorded");

    private static void ShouldEscalateForThePlan(GuardrailOutcome outcome)
    {
        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        outcome.Reasons.ShouldContain(EscalationReason.ProtectionPlanToCheck);
        outcome.Action.ShouldNotBeNull().Kind.ShouldBe(ActionKind.EscalateToReview);
        var check = PlanCheck(outcome);
        check.Passed.ShouldBeFalse();
        check.Stage.ShouldBe("BusinessRules");
    }
}
