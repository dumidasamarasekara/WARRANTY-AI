using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Remedies;
using Warranty.UnitTests.Domain;

namespace Warranty.UnitTests.Guardrails.Remedies;

/// <summary>
/// The deterministic remedy evaluator (002 plan.md "Remedy evaluation", research R2, R13) on the seeded Aurora
/// and Borealis rule sets (tasks.md "Fixed identifiers") and on the boundaries of FR-003 – FR-006, FR-012,
/// FR-031 and FR-032; with feature 003 also the <c>replacementNotAvailable</c> row (003 FR-026, research R2).
/// </summary>
public sealed class RemedyPolicyEvaluatorTests
{
    private const string ReplacementNotAvailablePending = "Pending T342";

    /// <summary>Claim value of BOR-SLATE11 in quickstart R10: 60% is 288.00.</summary>
    private const decimal SlateClaimValue = 480m;

    private static readonly RemedyPolicyEvaluator Evaluator = new();

    private static readonly RemedyRules Aurora = RemedyRulesContract.Aurora;

    private static readonly RemedyRules Borealis = RemedyRulesContract.Borealis;

    // ---- Covered defect and protection plan (initial scenario, R13) ------------------------------------

    [Fact]
    public void A_covered_defect_gets_Repair_set_by_policy_at_both_companies()
    {
        foreach (var rules in new[] { Aurora, Borealis })
        {
            Expect(Evaluator.Evaluate(rules, RemedyScenario.CoveredDefect), "coveredDefect", [RemedyType.Repair], single: RemedyType.Repair);
        }
    }

    [Fact]
    public void A_protection_plan_that_lifts_the_exclusion_gives_its_remedy()
        => Expect(
            Evaluator.Evaluate(Borealis, RemedyScenario.ProtectionPlanDamage), "protectionPlan", [RemedyType.Repair], single: RemedyType.Repair);

    [Fact]
    public void A_protection_plan_that_does_not_lift_the_exclusion_gives_no_remedy_and_needs_a_reviewer()
        => Expect(
            Evaluator.Evaluate(Aurora, RemedyScenario.ProtectionPlanDamage), "protectionPlan", [],
            reason: RemedyPendingReason.NoRemedyRule);

    [Fact]
    public void An_allowed_list_needs_a_reviewer_and_gives_no_recommendation()
    {
        var rules = new RemedyRules(
            new RemedyChoice(null, [RemedyType.Refund, RemedyType.Replacement]), null, null,
            new ProtectionPlanRule(true, null, [RemedyType.Repair, RemedyType.Replacement]));

        Expect(
            Evaluator.Evaluate(rules, RemedyScenario.CoveredDefect), "coveredDefect", [RemedyType.Refund, RemedyType.Replacement],
            reason: RemedyPendingReason.PolicyChoice);
        Expect(
            Evaluator.Evaluate(rules, RemedyScenario.ProtectionPlanDamage), "protectionPlan", [RemedyType.Repair, RemedyType.Replacement],
            reason: RemedyPendingReason.PolicyChoice);
    }

    // ---- Missing rules, rows or information (FR-012) -------------------------------------------------

    [Theory]
    [InlineData(RemedyScenario.CoveredDefect, "coveredDefect")]
    [InlineData(RemedyScenario.ProtectionPlanDamage, "protectionPlan")]
    [InlineData(RemedyScenario.RepairFailed, "repairFailed")]
    [InlineData(RemedyScenario.RepairNotEconomical, "repairNotEconomical")]
    public void A_version_without_remedy_rules_sends_every_scenario_to_a_reviewer(RemedyScenario scenario, string row)
        => Expect(
            Evaluator.Evaluate(null, scenario, failedAttempts: 2, estimate: 400m, claimValue: SlateClaimValue), row, [],
            reason: RemedyPendingReason.NoRemedyRule);

    [Fact]
    public void A_missing_row_sends_the_scenario_to_a_reviewer()
    {
        var empty = new RemedyRules(null, null, null, null);

        Expect(Evaluator.Evaluate(empty, RemedyScenario.CoveredDefect), "coveredDefect", [], reason: RemedyPendingReason.NoRemedyRule);
        Expect(Evaluator.Evaluate(empty, RemedyScenario.ProtectionPlanDamage), "protectionPlan", [], reason: RemedyPendingReason.NoRemedyRule);
        Expect(
            Evaluator.Evaluate(empty, RemedyScenario.RepairFailed, failedAttempts: 1), "repairFailed", [],
            reason: RemedyPendingReason.NoRemedyRule);
    }

    [Fact]
    public void Without_an_economic_limit_any_estimate_keeps_the_repair_going()
        => Expect(
            Evaluator.Evaluate(Aurora, RemedyScenario.RepairNotEconomical, estimate: 10_000m, claimValue: 450m), "repairNotEconomical", [],
            continueRepair: true);

    [Theory]
    [InlineData(null, 480.0)]
    [InlineData(300.0, null)]
    public void A_missing_estimate_or_claim_value_sends_the_remedy_to_a_reviewer_instead_of_guessing(double? estimate, double? claimValue)
        => Expect(
            Evaluator.Evaluate(Borealis, RemedyScenario.RepairNotEconomical, estimate: (decimal?)estimate, claimValue: (decimal?)claimValue),
            "repairNotEconomical", [RemedyType.Refund, RemedyType.Replacement], reason: RemedyPendingReason.NoRemedyRule,
            limit: claimValue is null ? null : 288.00m);

    // ---- Repair failed (FR-005, FR-030, FR-031) ------------------------------------------------------

    [Fact]
    public void One_failed_attempt_below_the_limit_of_two_continues_the_repair()
    {
        Expect(Evaluator.Evaluate(Aurora, RemedyScenario.RepairFailed, failedAttempts: 1), "repairFailed", [], continueRepair: true);
        Expect(Evaluator.Evaluate(Borealis, RemedyScenario.RepairFailed, failedAttempts: 1), "repairFailed", [], continueRepair: true);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void At_or_above_the_limit_the_repair_failed_row_is_recommended_and_a_reviewer_confirms_it(int failedAttempts)
    {
        var aurora = Evaluator.Evaluate(Aurora, RemedyScenario.RepairFailed, failedAttempts);
        Expect(aurora, "repairFailed", [RemedyType.Replacement], single: RemedyType.Replacement, reason: RemedyPendingReason.RepairFailedLimit);
        aurora.AutomaticRemedy.ShouldBeNull("a single remedy at the attempt limit still needs a reviewer (FR-031)");

        Expect(
            Evaluator.Evaluate(Borealis, RemedyScenario.RepairFailed, failedAttempts), "repairFailed", [RemedyType.Refund],
            single: RemedyType.Refund, reason: RemedyPendingReason.RepairFailedLimit);
    }

    // ---- Repair not economical (FR-004, FR-032, quickstart R10) ---------------------------------------

    [Theory]
    [InlineData(283.20)] // 59%
    [InlineData(288.00)] // exactly 60%: economical
    public void An_estimate_at_or_below_the_limit_keeps_the_repair_going(double estimate)
        => Expect(
            Evaluator.Evaluate(Borealis, RemedyScenario.RepairNotEconomical, estimate: (decimal)estimate, claimValue: SlateClaimValue),
            "repairNotEconomical", [], continueRepair: true, limit: 288.00m);

    [Fact]
    public void An_estimate_strictly_above_the_limit_offers_the_row_without_a_recommendation()
        => Expect(
            Evaluator.Evaluate(Borealis, RemedyScenario.RepairNotEconomical, estimate: 288.01m, claimValue: SlateClaimValue),
            "repairNotEconomical", [RemedyType.Refund, RemedyType.Replacement], reason: RemedyPendingReason.RepairNotEconomical,
            limit: 288.00m);

    [Fact]
    public void A_single_not_economical_remedy_is_recommended_but_still_confirmed_by_a_reviewer()
    {
        var rules = new RemedyRules(null, new RepairNotEconomicalRule(50, RemedyType.Replacement, null), null, null);

        var result = Evaluator.Evaluate(rules, RemedyScenario.RepairNotEconomical, estimate: 250.01m, claimValue: 500m);

        Expect(
            result, "repairNotEconomical", [RemedyType.Replacement], single: RemedyType.Replacement,
            reason: RemedyPendingReason.RepairNotEconomical, limit: 250.00m);
        result.AutomaticRemedy.ShouldBeNull();
    }

    // ---- Replacement not available (003 FR-026, research R2, quickstart X12) --------------------------

    [Fact(Skip = ReplacementNotAvailablePending)]
    public void The_replacement_not_available_row_is_read_under_its_own_name()
        => RemedyPolicyEvaluator.TableRowOf(RemedyScenario.ReplacementNotAvailable).ShouldBe("replacementNotAvailable");

    [Fact(Skip = ReplacementNotAvailablePending)]
    public void A_replacement_not_available_recommends_Refund_at_both_companies_and_a_reviewer_confirms_it()
    {
        foreach (var rules in new[] { RemedyRulesContract.AuroraWithReplacement, RemedyRulesContract.BorealisWithReplacement })
        {
            var result = Evaluator.Evaluate(rules, RemedyScenario.ReplacementNotAvailable);

            Expect(
                result, "replacementNotAvailable", [RemedyType.Refund], single: RemedyType.Refund,
                reason: RemedyPendingReason.ReplacementNotAvailable);
            result.AutomaticRemedy.ShouldBeNull("a single remedy after a replacement that is not available still needs a reviewer (FR-026)");
        }
    }

    [Fact(Skip = ReplacementNotAvailablePending)]
    public void A_replacement_not_available_allowed_list_is_offered_to_the_reviewer_without_a_recommendation()
    {
        var rules = RemedyRulesContract.AuroraWithReplacement with
        {
            ReplacementNotAvailable = new RemedyChoice(null, [RemedyType.Refund, RemedyType.Repair]),
        };

        Expect(
            Evaluator.Evaluate(rules, RemedyScenario.ReplacementNotAvailable), "replacementNotAvailable",
            [RemedyType.Refund, RemedyType.Repair], reason: RemedyPendingReason.ReplacementNotAvailable);
    }

    [Fact(Skip = ReplacementNotAvailablePending)]
    public void A_missing_replacement_not_available_row_sends_the_remedy_to_a_reviewer_without_a_rule()
    {
        // The 002 rule sets have no replacementNotAvailable row (and no replacement rules).
        foreach (var rules in new[] { Aurora, Borealis, RemedyRulesContract.AuroraWithReplacement with { ReplacementNotAvailable = null } })
        {
            Expect(
                Evaluator.Evaluate(rules, RemedyScenario.ReplacementNotAvailable), "replacementNotAvailable", [],
                reason: RemedyPendingReason.NoRemedyRule);
        }
    }

    [Fact(Skip = ReplacementNotAvailablePending)]
    public void A_version_without_remedy_rules_sends_a_replacement_not_available_to_a_reviewer()
        => Expect(
            Evaluator.Evaluate(null, RemedyScenario.ReplacementNotAvailable), "replacementNotAvailable", [],
            reason: RemedyPendingReason.NoRemedyRule);

    [Fact(Skip = ReplacementNotAvailablePending)]
    public void A_replacement_not_available_ignores_the_repair_facts()
        => Expect(
            Evaluator.Evaluate(
                RemedyRulesContract.BorealisWithReplacement, RemedyScenario.ReplacementNotAvailable, failedAttempts: 3, estimate: 400m,
                claimValue: SlateClaimValue),
            "replacementNotAvailable", [RemedyType.Refund], single: RemedyType.Refund, reason: RemedyPendingReason.ReplacementNotAvailable);

    // ---- Determinism (FR-010) -------------------------------------------------------------------------

    [Fact]
    public void Identical_inputs_give_identical_results()
    {
        foreach (var (rules, scenario, attempts, estimate) in new (RemedyRules, RemedyScenario, int, decimal?)[]
                 {
                     (Aurora, RemedyScenario.CoveredDefect, 0, null),
                     (Borealis, RemedyScenario.RepairNotEconomical, 0, 288.01m),
                     (Aurora, RemedyScenario.RepairFailed, 2, null),
                 })
        {
            var first = new RemedyPolicyEvaluator().Evaluate(rules, scenario, attempts, estimate, SlateClaimValue);
            var second = new RemedyPolicyEvaluator().Evaluate(rules, scenario, attempts, estimate, SlateClaimValue);

            second.ShouldBe(first with { AllowedRemedies = second.AllowedRemedies });
            second.AllowedRemedies.ShouldBe(first.AllowedRemedies);
        }
    }

    /// <summary>Asserts every field of an evaluation; anything not named is the "set automatically" default.</summary>
    private static void Expect(
        RemedyEvaluation result,
        string row,
        RemedyType[] allowed,
        RemedyType? single = null,
        RemedyPendingReason? reason = null,
        bool continueRepair = false,
        decimal? limit = null)
    {
        result.TableRow.ShouldBe(row);
        result.AllowedRemedies.ShouldBe(allowed);
        result.SingleRemedy.ShouldBe(single);
        result.PendingReason.ShouldBe(reason);
        result.NeedsReviewer.ShouldBe(reason is not null, "a reviewer is needed exactly when there is a pending reason");
        result.ContinueRepair.ShouldBe(continueRepair);
        result.EconomicLimitAmount.ShouldBe(limit);
        result.AutomaticRemedy.ShouldBe(reason is null && !continueRepair ? single : null);
    }
}
