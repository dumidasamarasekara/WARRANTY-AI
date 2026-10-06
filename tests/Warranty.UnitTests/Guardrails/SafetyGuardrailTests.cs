using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Domain.Policies;
using Warranty.Guardrails;

namespace Warranty.UnitTests.Guardrails;

/// <summary>
/// Safety guardrails of User Story 4 (spec FR-019, FR-022, FR-025, FR-028, SC-010, research R14, R26):
/// the AI disagreeing with the deterministic checks, citing what the terms do not contain or what was
/// not issued, or any manipulation signal, is never finalized automatically.
/// </summary>
public sealed class SafetyGuardrailTests
{
    // ── AI APPROVE vs an expired deterministic coverage window (US4 scenario 1) ─────────────────

    [Theory]
    [InlineData(92)]
    [InlineData(100)]
    public void An_AI_approval_after_the_deterministic_coverage_window_ended_goes_to_review(int confidence)
    {
        var scenario = ExpiredWindowApproval();
        scenario.Confidence = confidence;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        outcome.Reasons.ShouldBe([EscalationReason.AiDeterministicDisagreement]);
        outcome.Reasons[0].Label().ShouldStartWith("AI recommendation conflicts with");
        Failed(outcome, GuardrailCheckCode.CoverageWindowAgrees).Actual!.ShouldContain("after coverage end");
        ShouldIssue(outcome, ActionKind.EscalateToReview, scenario);
    }

    [Fact(Skip = "Pending T096")]
    public void The_coverage_conflict_is_reported_as_a_conflict_with_the_coverage_period_check()
    {
        var outcome = ExpiredWindowApproval().Evaluate();

        Failed(outcome, GuardrailCheckCode.CoverageWindowAgrees).Message!
            .ShouldContain("AI recommendation conflicts with coverage-period check");
    }

    // ── AI cites an exclusion the deterministic terms do not contain (R26) ──────────────────────

    [Fact]
    public void A_rejection_on_an_exclusion_the_applicable_terms_do_not_list_is_not_auto_rejected()
    {
        var scenario = GuardrailScenario.RejectOnExclusion(GuardrailScenario.UnlistedCosmeticExclusion, "COSMETIC_WEAR");

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        outcome.Reasons.ShouldContain(EscalationReason.AiDeterministicDisagreement);
        Failed(outcome, GuardrailCheckCode.GroundedInClause).Actual!.ShouldContain("not listed in the version's terms");
        ShouldIssue(outcome, ActionKind.EscalateToReview, scenario);
    }

    [Fact]
    public void A_rejection_on_an_exclusion_of_another_policy_version_is_not_auto_rejected()
    {
        var scenario = GuardrailScenario.RejectOnExclusion(GuardrailScenario.OtherVersionAccidentalExclusion, "CRACKED_SCREEN");

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        outcome.Reasons.ShouldContain(EscalationReason.AiDeterministicDisagreement);
        Failed(outcome, GuardrailCheckCode.GroundedInClause).Actual!.ShouldContain("not a clause of the applicable version");
    }

    [Fact]
    public void A_rejection_on_the_accidental_damage_exclusion_when_the_terms_cover_accidental_damage_is_not_auto_rejected()
    {
        // Tenant B's terms cover accidental damage, so ACCIDENTAL_DAMAGE is not in terms.exclusions even
        // though the photo shows a cracked screen and an exclusion clause with that code was retrieved.
        var scenario = GuardrailScenario.ApproveAccidentalDamage();
        scenario.Version.Terms.Excludes(ExclusionCode.AccidentalDamage).ShouldBeFalse();
        scenario.Decision = AiDecision.Reject;
        scenario.Coverage = CoverageDetermination.NotCovered;
        scenario.ClaimantExplanation = "Your tablet's screen damage is excluded from the warranty, so this repair is not covered.";
        scenario.PolicyCitations.Clear();
        scenario.PolicyCitations.Add(new PolicyCitation(GuardrailScenario.AccidentalExclusion, PolicyRefRelevance.SupportsRejection));

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        outcome.Reasons.ShouldContain(EscalationReason.AiDeterministicDisagreement);
        Failed(outcome, GuardrailCheckCode.GroundedInClause).Actual!.ShouldContain("not listed in the version's terms");
    }

    // ── Unissued references (US4 scenario 3, FR-022) ───────────────────────────────────────────

    [Theory]
    [InlineData(AiDecision.Approve, PolicyRefRelevance.SupportsCoverage)]
    [InlineData(AiDecision.Reject, PolicyRefRelevance.SupportsRejection)]
    public void A_cited_policy_reference_not_issued_for_the_run_fails_REFERENCES_VALID(AiDecision decision, PolicyRefRelevance relevance)
    {
        var scenario = decision == AiDecision.Reject ? GuardrailScenario.ClearReject() : GuardrailScenario.ClearApprove();
        scenario.PolicyCitations.Add(new PolicyCitation("POL-99", relevance));

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        outcome.Reasons.ShouldContain(EscalationReason.InvalidRecommendation);
        Failed(outcome, GuardrailCheckCode.ReferencesValid).Actual!.ShouldContain("POL-99");
        ShouldIssue(outcome, ActionKind.EscalateToReview, scenario);
    }

    [Fact]
    public void A_retrieved_clause_reference_that_was_never_issued_to_the_model_fails_REFERENCES_VALID()
    {
        var scenario = GuardrailScenario.ClearApprove();
        scenario.IssuedReferences.Remove(GuardrailScenario.CoverageClause);

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        outcome.Reasons.ShouldContain(EscalationReason.InvalidRecommendation);
        Failed(outcome, GuardrailCheckCode.ReferencesValid).Actual!.ShouldContain(GuardrailScenario.CoverageClause);
    }

    // ── Any manipulation signal (US4 scenario 2, FR-019, SC-010) ────────────────────────────────

    public static TheoryData<AiDecision, string> ManipulationCases()
    {
        var data = new TheoryData<AiDecision, string>();
        foreach (var decision in new[] { AiDecision.Approve, AiDecision.Reject, AiDecision.RequestMoreInformation })
        {
            foreach (var kind in new[] { "deterministic-signal", "ai-signal", "ai-flag", "signal-and-flag", "high-risk-signal" })
            {
                data.Add(decision, kind);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ManipulationCases))]
    public void Any_manipulation_signal_prevents_automatic_approval_and_rejection(AiDecision decision, string kind)
    {
        var scenario = decision == AiDecision.Reject ? GuardrailScenario.ClearReject() : GuardrailScenario.ClearApprove();
        scenario.Decision = decision;
        if (decision == AiDecision.RequestMoreInformation)
        {
            scenario.AiMissingInformation.Add(new RequestedItem(RequestedItemCodes.PhotoOfDamage, "No photo shows the reported defect."));
        }

        // The model still claims the claim is low risk and is fully confident.
        scenario.AiReportedRiskLevel = "LOW";
        scenario.Confidence = 100;
        switch (kind)
        {
            case "deterministic-signal":
                scenario.AddRiskSignal(RiskSignalCode.ManipulationAttempt, RiskSignalSource.Deterministic);
                break;
            case "ai-signal":
                scenario.AddRiskSignal(RiskSignalCode.ManipulationAttempt, RiskSignalSource.Ai);
                break;
            case "ai-flag":
                scenario.ManipulationDetected = true;
                break;
            case "signal-and-flag":
                scenario.AddRiskSignal(RiskSignalCode.ManipulationAttempt, RiskSignalSource.Deterministic);
                scenario.ManipulationDetected = true;
                break;
            case "high-risk-signal":
                scenario.AddRiskSignal(RiskSignalCode.ManipulationAttempt, RiskSignalSource.Deterministic);
                scenario.SignalledRiskLevel = RiskLevel.High;
                break;
        }

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldNotBe(Disposition.AutoApprove);
        outcome.Disposition.ShouldNotBe(Disposition.AutoReject);
        outcome.Disposition.ShouldBe(Disposition.HumanReview, $"{decision} with {kind}");
        outcome.Reasons.ShouldNotBeEmpty();
        Failed(outcome, GuardrailCheckCode.NoManipulation);
        ShouldIssue(outcome, ActionKind.EscalateToReview, scenario);
        outcome.Action!.Kind.ShouldNotBe(ActionKind.FinalizeApproved);
        outcome.Action.Kind.ShouldNotBe(ActionKind.FinalizeRejected);
    }

    [Fact]
    public void A_clear_claim_without_any_manipulation_signal_passes_NO_MANIPULATION()
    {
        var outcome = GuardrailScenario.ClearApprove().Evaluate();

        outcome.Disposition.ShouldBe(Disposition.AutoApprove);
        outcome.Checks.ShouldContain(check => check.Code == GuardrailCheckCode.NoManipulation && check.Passed);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A clear approval whose claim date lies after the deterministic coverage end; the AI still reads it as covered.</summary>
    private static GuardrailScenario ExpiredWindowApproval()
    {
        var scenario = GuardrailScenario.ClearApprove();
        scenario.CoverageEndDate = new DateOnly(2025, 6, 1);
        scenario.WithinCoverageWindow = false;
        return scenario;
    }

    private static GuardrailCheck Failed(GuardrailOutcome outcome, GuardrailCheckCode code)
    {
        var check = outcome.Checks.Single(c => c.Code == code);
        check.Passed.ShouldBeFalse($"{code} should have failed");
        return check;
    }

    private static void ShouldIssue(GuardrailOutcome outcome, ActionKind kind, GuardrailScenario scenario)
    {
        var action = outcome.Action.ShouldNotBeNull();
        action.Kind.ShouldBe(kind);
        action.TenantId.ShouldBe(GuardrailScenario.Tenant);
        action.ClaimId.ShouldBe(scenario.ClaimId);
        action.RunId.ShouldBe(scenario.RunId);
    }
}
