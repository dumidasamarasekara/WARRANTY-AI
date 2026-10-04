using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Domain.Policies;
using Warranty.Guardrails;
using Warranty.Guardrails.Rules;

namespace Warranty.UnitTests.Guardrails;

/// <summary>
/// Deterministic grounding of an AI <c>REJECT</c> (spec FR-027, research R26): the
/// <c>GROUNDED_IN_CLAUSE</c> check passes only for a cited period clause the coverage window confirms,
/// or a cited exclusion clause of the applicable version whose code is in <c>terms.exclusions</c> and
/// is evidenced by a photo damage type through <see cref="ExclusionEvidenceMap"/>.
/// </summary>
public sealed class ExclusionGroundingTests
{
    /// <summary>Every damage type of photo-analysis.schema.json.</summary>
    private static readonly string[] AllDamageTypes =
    [
        "CRACKED_SCREEN", "DENTS_OR_IMPACT", "LIQUID_INDICATORS", "BURN_OR_SCORCH", "CORROSION", "COSMETIC_WEAR",
        "MISSING_PARTS", "NONE_VISIBLE", "OTHER",
    ];

    [Fact]
    public void The_exclusion_fixtures_are_what_the_tests_assume()
    {
        var scenario = GuardrailScenario.RejectOnExclusion(GuardrailScenario.AccidentalExclusion, "CRACKED_SCREEN");
        var clauses = scenario.Clauses.ToDictionary(c => c.RefId);

        var input = scenario.Build();

        input.Recommendation.ShouldNotBeNull().Decision.ShouldBe(AiDecision.Reject);
        input.Recommendation.PolicyRefs.ShouldHaveSingleItem()
            .ShouldBe(new PolicyCitation(GuardrailScenario.AccidentalExclusion, PolicyRefRelevance.SupportsRejection));
        input.Evidence.ShouldNotBeNull().Photos.ShouldHaveSingleItem().DamageTypes.ShouldBe(["CRACKED_SCREEN"]);
        input.Policy.ShouldNotBeNull().Version.ShouldBe(scenario.Version);
        input.Policy.CoverageWindow.WithinComponentCoverage.ShouldBe(true);

        var terms = scenario.Version.Terms;
        clauses[GuardrailScenario.AccidentalExclusion].ExclusionCode.ShouldBe(ExclusionCode.AccidentalDamage);
        clauses[GuardrailScenario.LiquidExclusion].ExclusionCode.ShouldBe(ExclusionCode.LiquidDamage);
        clauses[GuardrailScenario.UnauthorizedRepairExclusion].ExclusionCode.ShouldBe(ExclusionCode.UnauthorizedRepair);
        terms.Excludes(ExclusionCode.AccidentalDamage).ShouldBeTrue();
        terms.Excludes(ExclusionCode.LiquidDamage).ShouldBeTrue();
        terms.Excludes(ExclusionCode.UnauthorizedRepair).ShouldBeTrue();
        terms.Excludes(ExclusionCode.CosmeticDamage).ShouldBeFalse();
        clauses[GuardrailScenario.UnlistedCosmeticExclusion].ExclusionCode.ShouldBe(ExclusionCode.CosmeticDamage);
        clauses[GuardrailScenario.UnlistedCosmeticExclusion].PolicyVersionId.ShouldBe(scenario.Version.Id);
        clauses[GuardrailScenario.OtherVersionAccidentalExclusion].PolicyVersionId.ShouldNotBe(scenario.Version.Id);
        clauses[GuardrailScenario.PeriodClause].ClauseType.ShouldBe(ClauseType.Period);
    }

    // ── GROUNDED_IN_CLAUSE through the engine ───────────────────────────────────────────────────

    [Fact]
    public void A_cited_listed_accidental_damage_exclusion_with_a_cracked_screen_photo_is_grounded()
    {
        var scenario = GuardrailScenario.RejectOnExclusion(GuardrailScenario.AccidentalExclusion, "CRACKED_SCREEN");

        var outcome = scenario.Evaluate();

        ShouldPass(outcome, GuardrailCheckCode.GroundedInClause);
        outcome.Disposition.ShouldBe(Disposition.AutoReject);
    }

    [Theory]
    [InlineData(GuardrailScenario.AccidentalExclusion, "DENTS_OR_IMPACT")]
    [InlineData(GuardrailScenario.LiquidExclusion, "LIQUID_INDICATORS")]
    [InlineData(GuardrailScenario.LiquidExclusion, "CORROSION")]
    public void Each_mapped_damage_type_grounds_its_exclusion(string clause, string damageType)
    {
        var outcome = GuardrailScenario.RejectOnExclusion(clause, damageType).Evaluate();

        ShouldPass(outcome, GuardrailCheckCode.GroundedInClause);
        outcome.Disposition.ShouldBe(Disposition.AutoReject);
    }

    [Theory]
    [InlineData("CRACKED_SCREEN")]
    [InlineData("NONE_VISIBLE")]
    [InlineData("OTHER")]
    public void A_liquid_exclusion_without_a_liquid_damage_photo_is_not_grounded(string damageType)
        => ShouldNotBeGrounded(GuardrailScenario.RejectOnExclusion(GuardrailScenario.LiquidExclusion, damageType));

    [Fact]
    public void An_exclusion_without_any_photo_damage_type_is_not_grounded()
        => ShouldNotBeGrounded(GuardrailScenario.RejectOnExclusion(GuardrailScenario.AccidentalExclusion));

    [Fact]
    public void An_exclusion_whose_code_is_not_in_the_versions_terms_is_not_grounded()
    {
        var scenario = GuardrailScenario.RejectOnExclusion(GuardrailScenario.UnlistedCosmeticExclusion, "COSMETIC_WEAR");
        scenario.Version.Terms.Excludes(ExclusionCode.CosmeticDamage).ShouldBeFalse("precondition");

        ShouldNotBeGrounded(scenario);
    }

    [Fact]
    public void An_exclusion_clause_of_another_policy_version_is_not_grounded()
    {
        var scenario = GuardrailScenario.RejectOnExclusion(GuardrailScenario.OtherVersionAccidentalExclusion, "CRACKED_SCREEN");
        scenario.Clauses.Single(c => c.RefId == GuardrailScenario.OtherVersionAccidentalExclusion)
            .PolicyVersionId.ShouldNotBe(scenario.Version.Id, "precondition");

        ShouldNotBeGrounded(scenario);
    }

    public static TheoryData<string> EveryDamageType => new(AllDamageTypes);

    [Theory]
    [MemberData(nameof(EveryDamageType))]
    public void An_unauthorized_repair_exclusion_is_never_grounded(string damageType)
        => ShouldNotBeGrounded(GuardrailScenario.RejectOnExclusion(GuardrailScenario.UnauthorizedRepairExclusion, damageType));

    [Fact]
    public void An_unauthorized_repair_exclusion_is_not_grounded_even_with_every_damage_type_reported()
        => ShouldNotBeGrounded(GuardrailScenario.RejectOnExclusion(GuardrailScenario.UnauthorizedRepairExclusion, AllDamageTypes));

    [Fact]
    public void An_exclusion_cited_only_as_context_does_not_ground_a_rejection()
    {
        var scenario = GuardrailScenario.RejectOnExclusion(GuardrailScenario.AccidentalExclusion, "CRACKED_SCREEN");
        scenario.PolicyCitations.Clear();
        scenario.PolicyCitations.Add(new PolicyCitation(GuardrailScenario.AccidentalExclusion, PolicyRefRelevance.Context));

        ShouldNotBeGrounded(scenario);
    }

    [Fact]
    public void One_grounded_citation_is_enough_when_another_cited_exclusion_is_not_evidenced()
    {
        var scenario = GuardrailScenario.RejectOnExclusion(GuardrailScenario.LiquidExclusion, "CRACKED_SCREEN");
        scenario.PolicyCitations.Add(new PolicyCitation(GuardrailScenario.AccidentalExclusion, PolicyRefRelevance.SupportsRejection));

        var outcome = scenario.Evaluate();

        ShouldPass(outcome, GuardrailCheckCode.GroundedInClause);
        outcome.Disposition.ShouldBe(Disposition.AutoReject);
    }

    [Fact]
    public void A_period_clause_is_grounded_when_the_deterministic_window_confirms_expiry()
    {
        var scenario = GuardrailScenario.ClearReject();
        scenario.WithinCoverageWindow.ShouldBeFalse("precondition");

        var outcome = scenario.Evaluate();

        ShouldPass(outcome, GuardrailCheckCode.GroundedInClause);
        outcome.Disposition.ShouldBe(Disposition.AutoReject);
    }

    [Fact]
    public void A_period_clause_is_not_grounded_when_the_claim_is_inside_the_deterministic_window()
    {
        var scenario = GuardrailScenario.ClearReject();
        scenario.CoverageEndDate = new DateOnly(2027, 1, 15);
        scenario.WithinCoverageWindow = true;

        ShouldNotBeGrounded(scenario);
    }

    // ── ExclusionEvidenceMap ────────────────────────────────────────────────────────────────────

    public static TheoryData<ExclusionCode, string, bool> MappingTable()
    {
        var supported = new Dictionary<ExclusionCode, string[]>
        {
            [ExclusionCode.AccidentalDamage] = ["CRACKED_SCREEN", "DENTS_OR_IMPACT"],
            [ExclusionCode.LiquidDamage] = ["LIQUID_INDICATORS", "CORROSION"],
            [ExclusionCode.CosmeticDamage] = ["COSMETIC_WEAR"],
            [ExclusionCode.UnauthorizedRepair] = [],
        };

        var data = new TheoryData<ExclusionCode, string, bool>();
        foreach (var (code, types) in supported)
        {
            foreach (var damageType in AllDamageTypes)
            {
                data.Add(code, damageType, types.Contains(damageType));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MappingTable))]
    public void The_map_supports_exactly_the_R26_damage_types(ExclusionCode code, string damageType, bool expected)
        => ExclusionEvidenceMap.IsSupported(code, [damageType]).ShouldBe(expected);

    [Theory]
    [InlineData(ExclusionCode.AccidentalDamage)]
    [InlineData(ExclusionCode.LiquidDamage)]
    [InlineData(ExclusionCode.CosmeticDamage)]
    [InlineData(ExclusionCode.UnauthorizedRepair)]
    public void No_photo_damage_type_supports_no_exclusion(ExclusionCode code)
        => ExclusionEvidenceMap.IsSupported(code, []).ShouldBeFalse();

    [Fact]
    public void One_matching_type_among_others_is_enough()
        => ExclusionEvidenceMap.IsSupported(ExclusionCode.AccidentalDamage, ["NONE_VISIBLE", "COSMETIC_WEAR", "DENTS_OR_IMPACT"]).ShouldBeTrue();

    [Fact]
    public void Unauthorized_repair_is_unsupported_even_with_every_damage_type()
        => ExclusionEvidenceMap.IsSupported(ExclusionCode.UnauthorizedRepair, AllDamageTypes).ShouldBeFalse();

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static void ShouldNotBeGrounded(GuardrailScenario scenario)
    {
        var outcome = scenario.Evaluate();

        outcome.Checks.ShouldContain(
            check => check.Code == GuardrailCheckCode.GroundedInClause && !check.Passed, "GROUNDED_IN_CLAUSE should have failed");
        outcome.Disposition.ShouldNotBe(Disposition.AutoReject);
        outcome.Disposition.ShouldBe(Disposition.HumanReview);
    }

    private static void ShouldPass(GuardrailOutcome outcome, GuardrailCheckCode code)
        => outcome.Checks.ShouldContain(check => check.Code == code && check.Passed, $"{code} should have passed");
}
