using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Remedies;
using Warranty.UnitTests.Domain;

namespace Warranty.UnitTests.Guardrails.Remedies;

/// <summary>
/// The deterministic replacement rules (003 research R2, R9) on the seeded Aurora and Borealis replacement rules
/// (tasks.md "Fixed identifiers") and on the boundaries of FR-002, FR-013 and FR-023: the initial status for both
/// return rules with and without a product held by a repair centre, and the equivalent-model check.
/// </summary>
public sealed class ReplacementPolicyTests
{
    private static readonly ReplacementPolicy Policy = new();

    /// <summary>Same category, at least the claim value, reviewer approves; returned at collection.</summary>
    private static readonly ReplacementRules Aurora = RemedyRulesContract.AuroraWithReplacement.Replacement!;

    /// <summary>Same model only; returned before reservation.</summary>
    private static readonly ReplacementRules Borealis = RemedyRulesContract.BorealisWithReplacement.Replacement!;

    /// <summary>The seeded Aurora tablet (claim value 450) and its seeded equivalent AUR-TAB11 (520).</summary>
    private static readonly ReplacementModel Tab10 = new("AUR-TAB10", "tablet", 450m);

    private static readonly ReplacementModel Tab11 = new("AUR-TAB11", "tablet", 520m);

    // ---- Initial status (FR-013, research R9) ------------------------------------------------------------

    [Theory]
    [InlineData(ReplacementReturnRule.AtCollection, false, ReplacementStatus.AwaitingUnit)]
    [InlineData(ReplacementReturnRule.AtCollection, true, ReplacementStatus.AwaitingUnit)]
    [InlineData(ReplacementReturnRule.BeforeReservation, false, ReplacementStatus.AwaitingReturn)]
    [InlineData(ReplacementReturnRule.BeforeReservation, true, ReplacementStatus.AwaitingUnit)]
    public void The_initial_status_awaits_the_return_only_before_reservation_without_a_held_product(
        ReplacementReturnRule returnRule, bool heldByRepairCentre, ReplacementStatus expected)
        => Policy.InitialStatus(new ReplacementRules(ReturnRule: returnRule), heldByRepairCentre).ShouldBe(expected);

    [Fact]
    public void Aurora_returns_at_collection_so_a_replacement_starts_awaiting_a_unit()
    {
        Policy.InitialStatus(Aurora, productHeldByRepairCentre: false).ShouldBe(ReplacementStatus.AwaitingUnit);
        Policy.InitialStatus(Aurora, productHeldByRepairCentre: true).ShouldBe(ReplacementStatus.AwaitingUnit);
    }

    [Fact]
    public void Borealis_returns_before_reservation_unless_a_repair_centre_already_holds_the_product()
    {
        Policy.InitialStatus(Borealis, productHeldByRepairCentre: false).ShouldBe(ReplacementStatus.AwaitingReturn);
        Policy.InitialStatus(Borealis, productHeldByRepairCentre: true).ShouldBe(ReplacementStatus.AwaitingUnit);
    }

    [Fact]
    public void A_version_without_replacement_rules_returns_at_collection()
    {
        Policy.InitialStatus(null, productHeldByRepairCentre: false).ShouldBe(ReplacementStatus.AwaitingUnit);
        Policy.InitialStatus(null, productHeldByRepairCentre: true).ShouldBe(ReplacementStatus.AwaitingUnit);
    }

    [Fact]
    public void The_initial_status_is_deterministic()
        => Policy.InitialStatus(Borealis, false).ShouldBe(Policy.InitialStatus(Borealis, false));

    // ---- Model check (FR-002, FR-023) ---------------------------------------------------------------------

    [Fact]
    public void The_same_model_is_allowed_under_both_product_rules_without_approval()
    {
        foreach (var rules in new[] { Aurora, Borealis })
        {
            Expect(Policy.CheckModel(rules, Tab10, Tab10), ReplacementModelVerdict.SameModel);
        }
    }

    [Fact]
    public void The_same_model_compares_model_codes_case_insensitively()
        => Expect(Policy.CheckModel(Aurora, Tab10, Tab10 with { ModelCode = "aur-tab10" }), ReplacementModelVerdict.SameModel);

    [Fact]
    public void The_same_model_is_allowed_even_without_a_claim_value()
    {
        var noValue = Tab10 with { ClaimValue = null };

        Expect(Policy.CheckModel(Aurora, noValue, noValue), ReplacementModelVerdict.SameModel);
    }

    [Fact]
    public void An_equivalent_with_a_higher_claim_value_needs_a_reviewer()
        => Expect(Policy.CheckModel(Aurora, Tab10, Tab11), ReplacementModelVerdict.NeedsReviewerApproval);

    [Fact]
    public void An_equivalent_with_exactly_the_original_claim_value_needs_a_reviewer()
        => Expect(
            Policy.CheckModel(Aurora, Tab10, Tab11 with { ClaimValue = Tab10.ClaimValue }), ReplacementModelVerdict.NeedsReviewerApproval);

    [Fact]
    public void An_equivalent_with_a_lower_claim_value_is_refused()
        => Expect(
            Policy.CheckModel(Aurora, Tab11, Tab10), ReplacementModelVerdict.Refused, ReplacementModelRefusal.LowerClaimValue);

    [Fact]
    public void An_equivalent_one_cent_below_the_original_claim_value_is_refused()
        => Expect(
            Policy.CheckModel(Aurora, Tab10, Tab11 with { ClaimValue = 449.99m }),
            ReplacementModelVerdict.Refused, ReplacementModelRefusal.LowerClaimValue);

    [Fact]
    public void A_model_of_another_category_is_refused_even_with_a_higher_claim_value()
        => Expect(
            Policy.CheckModel(Aurora, Tab10, new ReplacementModel("AUR-LAP14", "laptop", 1200m)),
            ReplacementModelVerdict.Refused, ReplacementModelRefusal.OtherCategory);

    [Fact]
    public void The_seeded_Aurora_phone_is_refused_on_a_tablet_claim()
        => Expect(
            Policy.CheckModel(Aurora, Tab10, new ReplacementModel("AUR-PHN6", "phone", 480m)),
            ReplacementModelVerdict.Refused, ReplacementModelRefusal.OtherCategory);

    [Fact]
    public void Categories_compare_case_insensitively()
        => Expect(Policy.CheckModel(Aurora, Tab10, Tab11 with { Category = "Tablet" }), ReplacementModelVerdict.NeedsReviewerApproval);

    [Fact]
    public void A_proposed_model_without_a_claim_value_is_refused()
        => Expect(
            Policy.CheckModel(Aurora, Tab10, Tab11 with { ClaimValue = null }),
            ReplacementModelVerdict.Refused, ReplacementModelRefusal.MissingClaimValue);

    [Fact]
    public void An_original_model_without_a_claim_value_allows_only_the_same_model()
        => Expect(
            Policy.CheckModel(Aurora, Tab10 with { ClaimValue = null }, Tab11),
            ReplacementModelVerdict.Refused, ReplacementModelRefusal.MissingClaimValue);

    [Fact]
    public void The_SameModel_rule_refuses_every_other_model()
    {
        var slate11 = new ReplacementModel("BOR-SLATE11", "tablet", 480m);
        var slate12 = new ReplacementModel("BOR-SLATE12", "tablet", 600m);

        Expect(Policy.CheckModel(Borealis, slate11, slate12), ReplacementModelVerdict.Refused, ReplacementModelRefusal.SameModelOnly);
        Expect(
            Policy.CheckModel(Borealis, slate11, new ReplacementModel("BOR-OVEN60", "major-appliance", 1650m)),
            ReplacementModelVerdict.Refused, ReplacementModelRefusal.SameModelOnly);
    }

    [Fact]
    public void A_version_without_replacement_rules_allows_the_same_model_only()
    {
        Expect(Policy.CheckModel(null, Tab10, Tab10), ReplacementModelVerdict.SameModel);
        Expect(Policy.CheckModel(null, Tab10, Tab11), ReplacementModelVerdict.Refused, ReplacementModelRefusal.SameModelOnly);
    }

    [Fact]
    public void The_model_check_is_deterministic()
        => Policy.CheckModel(Aurora, Tab10, Tab11).ShouldBe(Policy.CheckModel(Aurora, Tab10, Tab11));

    private static void Expect(ModelCheckResult check, ReplacementModelVerdict verdict, ReplacementModelRefusal? refusal = null)
    {
        check.Verdict.ShouldBe(verdict);
        check.Refusal.ShouldBe(refusal);
        check.Allowed.ShouldBe(verdict != ReplacementModelVerdict.Refused);
    }
}
