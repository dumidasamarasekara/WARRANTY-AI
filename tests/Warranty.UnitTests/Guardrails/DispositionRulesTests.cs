using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Guardrails;

namespace Warranty.UnitTests.Guardrails;

/// <summary>
/// Disposition rules of the guardrail engine (spec FR-026 – FR-028, contracts/agents-and-tools.md
/// disposition table, research R23 – R25): automatic finalization only when every condition holds.
/// </summary>
public sealed class DispositionRulesTests
{
    private const string PendingEngine = "Pending T066";

    [Theory]
    [InlineData(AiDecision.Approve)]
    [InlineData(AiDecision.Reject)]
    public void The_clear_cut_scenarios_build_a_consistent_input(AiDecision decision)
    {
        var input = Clear(decision).Build();

        input.Recommendation.ShouldNotBeNull().IsValid.ShouldBeTrue();
        input.Recommendation.Decision.ShouldBe(decision);
        input.Risk.ShouldNotBeNull().Level.ShouldBe(RiskLevel.Low);
        input.Policy.ShouldNotBeNull().Version.ShouldNotBeNull();
        input.Policy.CoverageWindow.WithinComponentCoverage.ShouldBe(decision == AiDecision.Approve);
        input.Recommendation.PolicyRefs.Select(r => r.Ref).ShouldAllBe(id => input.IssuedReferences.Contains(id));
        input.Recommendation.EvidenceRefs.Select(r => r.Ref).ShouldAllBe(id => input.IssuedReferences.Contains(id));
        input.Settings.IsAboveAutoApprovalLimit(input.Case.ClaimValue!.Value).ShouldBeFalse();
        input.Settings.AlwaysRequiresReview(input.Case.ProductCategory).ShouldBeFalse();
    }

    // ── AutoApprove (FR-026) ────────────────────────────────────────────────────────────────────

    [Fact(Skip = PendingEngine)]
    public void A_claim_meeting_every_FR026_condition_is_auto_approved()
    {
        var scenario = GuardrailScenario.ClearApprove();

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.AutoApprove);
        outcome.Checks.ShouldAllBe(check => check.Passed);
        outcome.Reasons.ShouldBeEmpty();
        ShouldIssue(outcome, ActionKind.FinalizeApproved, scenario);
    }

    public static TheoryData<string, GuardrailCheckCode?, EscalationReason?> ApproveConditionFlips => new()
    {
        { "ai-recommends-review", null, EscalationReason.AiRecommendsReview },
        { "recommendation-invalid", GuardrailCheckCode.SchemaValid, EscalationReason.InvalidRecommendation },
        { "ai-unavailable", null, EscalationReason.AiUnavailable },
        { "unknown-reference", GuardrailCheckCode.ReferencesValid, null },
        { "no-supporting-policy-ref", null, null },
        { "confidence-below-min", GuardrailCheckCode.ConfidenceAtOrAboveMin, EscalationReason.ConfidenceBelowMin },
        { "risk-signal", GuardrailCheckCode.RiskLow, EscalationReason.RiskMedium },
        { "value-above-limit", GuardrailCheckCode.ClaimValueWithinLimit, EscalationReason.ValueAboveLimit },
        { "coverage-window-disagrees", GuardrailCheckCode.CoverageWindowAgrees, EscalationReason.AiDeterministicDisagreement },
        { "always-review-category", GuardrailCheckCode.CategoryNotAlwaysReview, EscalationReason.AlwaysReviewCategory },
        { "auto-approve-disabled", GuardrailCheckCode.AutoDecisionEnabled, null },
        { "returned-from-review", GuardrailCheckCode.NotReturnedFromReview, EscalationReason.ReturnedAfterReviewerRequest },
        { "claimant-text-unsafe", GuardrailCheckCode.ClaimantTextSafe, EscalationReason.UnsafeClaimantText },
    };

    [Theory(Skip = PendingEngine)]
    [MemberData(nameof(ApproveConditionFlips))]
    public void Flipping_any_FR026_condition_prevents_auto_approval(
        string flip, GuardrailCheckCode? failedCheck, EscalationReason? reason)
    {
        var scenario = GuardrailScenario.ClearApprove();
        Apply(scenario, flip);

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview, flip);
        ShouldIssue(outcome, ActionKind.EscalateToReview, scenario);
        if (failedCheck is { } code)
        {
            ShouldFail(outcome, code);
        }

        if (reason is { } expected)
        {
            outcome.Reasons.ShouldContain(expected);
        }
    }

    // ── AutoReject (FR-027) ─────────────────────────────────────────────────────────────────────

    [Fact(Skip = PendingEngine)]
    public void A_claim_meeting_every_FR027_condition_is_auto_rejected()
    {
        var scenario = GuardrailScenario.ClearReject();

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.AutoReject);
        outcome.Checks.ShouldAllBe(check => check.Passed);
        outcome.Reasons.ShouldBeEmpty();
        ShouldIssue(outcome, ActionKind.FinalizeRejected, scenario);
    }

    public static TheoryData<string, GuardrailCheckCode?, EscalationReason?> RejectConditionFlips => new()
    {
        { "ai-recommends-review", null, EscalationReason.AiRecommendsReview },
        { "recommendation-invalid", GuardrailCheckCode.SchemaValid, EscalationReason.InvalidRecommendation },
        { "ai-unavailable", null, EscalationReason.AiUnavailable },
        { "unknown-reference", GuardrailCheckCode.ReferencesValid, null },
        { "confidence-below-min", GuardrailCheckCode.ConfidenceAtOrAboveMin, EscalationReason.ConfidenceBelowMin },
        { "risk-signal", GuardrailCheckCode.RiskLow, EscalationReason.RiskMedium },
        { "value-above-limit", GuardrailCheckCode.ClaimValueWithinLimit, EscalationReason.ValueAboveLimit },
        { "always-review-category", GuardrailCheckCode.CategoryNotAlwaysReview, EscalationReason.AlwaysReviewCategory },
        { "auto-reject-disabled", GuardrailCheckCode.AutoDecisionEnabled, null },
        { "period-not-confirmed-by-window", GuardrailCheckCode.GroundedInClause, null },
        { "only-coverage-clause-cited", GuardrailCheckCode.GroundedInClause, null },
        { "returned-from-review", GuardrailCheckCode.NotReturnedFromReview, EscalationReason.ReturnedAfterReviewerRequest },
        { "claimant-text-unsafe", GuardrailCheckCode.ClaimantTextSafe, EscalationReason.UnsafeClaimantText },
    };

    [Theory(Skip = PendingEngine)]
    [MemberData(nameof(RejectConditionFlips))]
    public void Flipping_any_FR027_condition_prevents_auto_rejection(
        string flip, GuardrailCheckCode? failedCheck, EscalationReason? reason)
    {
        var scenario = GuardrailScenario.ClearReject();
        Apply(scenario, flip);

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview, flip);
        ShouldIssue(outcome, ActionKind.EscalateToReview, scenario);
        if (failedCheck is { } code)
        {
            ShouldFail(outcome, code);
        }

        if (reason is { } expected)
        {
            outcome.Reasons.ShouldContain(expected);
        }
    }

    // ── Boundaries ──────────────────────────────────────────────────────────────────────────────

    [Theory(Skip = PendingEngine)]
    [InlineData(AiDecision.Approve, Disposition.AutoApprove)]
    [InlineData(AiDecision.Reject, Disposition.AutoReject)]
    public void A_claim_value_equal_to_the_limit_is_within_it(AiDecision decision, Disposition expected)
    {
        var scenario = Clear(decision);
        scenario.ClaimValue = scenario.AutoApprovalLimit;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(expected);
        ShouldPass(outcome, GuardrailCheckCode.ClaimValueWithinLimit);
    }

    [Theory(Skip = PendingEngine)]
    [InlineData(AiDecision.Approve)]
    [InlineData(AiDecision.Reject)]
    public void A_claim_value_one_cent_above_the_limit_goes_to_review(AiDecision decision)
    {
        var scenario = Clear(decision);
        scenario.ClaimValue = scenario.AutoApprovalLimit + 0.01m;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        ShouldFail(outcome, GuardrailCheckCode.ClaimValueWithinLimit);
        outcome.Reasons.ShouldContain(EscalationReason.ValueAboveLimit);
    }

    [Theory(Skip = PendingEngine)]
    [InlineData(AiDecision.Approve, Disposition.AutoApprove)]
    [InlineData(AiDecision.Reject, Disposition.AutoReject)]
    public void A_confidence_equal_to_the_minimum_is_allowed(AiDecision decision, Disposition expected)
    {
        var scenario = Clear(decision);
        scenario.Confidence = scenario.MinConfidence;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(expected);
        ShouldPass(outcome, GuardrailCheckCode.ConfidenceAtOrAboveMin);
    }

    [Theory(Skip = PendingEngine)]
    [InlineData(AiDecision.Approve)]
    [InlineData(AiDecision.Reject)]
    public void A_confidence_one_below_the_minimum_goes_to_review(AiDecision decision)
    {
        var scenario = Clear(decision);
        scenario.Confidence = scenario.MinConfidence - 1;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        ShouldFail(outcome, GuardrailCheckCode.ConfidenceAtOrAboveMin);
        outcome.Reasons.ShouldContain(EscalationReason.ConfidenceBelowMin);
    }

    // ── Tenant switches ─────────────────────────────────────────────────────────────────────────

    [Fact(Skip = PendingEngine)]
    public void With_auto_approve_disabled_a_clear_approval_goes_to_review()
    {
        var scenario = GuardrailScenario.ClearApprove();
        scenario.AutoApproveEnabled = false;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        ShouldFail(outcome, GuardrailCheckCode.AutoDecisionEnabled);
        outcome.Reasons.ShouldNotBeEmpty();
    }

    [Fact(Skip = PendingEngine)]
    public void With_auto_reject_disabled_a_clear_rejection_goes_to_review()
    {
        var scenario = GuardrailScenario.ClearReject();
        scenario.AutoRejectEnabled = false;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        ShouldFail(outcome, GuardrailCheckCode.AutoDecisionEnabled);
        outcome.Reasons.ShouldNotBeEmpty();
    }

    [Fact(Skip = PendingEngine)]
    public void Each_switch_only_governs_its_own_decision()
    {
        var approve = GuardrailScenario.ClearApprove();
        approve.AutoRejectEnabled = false;
        var reject = GuardrailScenario.ClearReject();
        reject.AutoApproveEnabled = false;

        approve.Evaluate().Disposition.ShouldBe(Disposition.AutoApprove);
        reject.Evaluate().Disposition.ShouldBe(Disposition.AutoReject);
    }

    // ── Risk: any signal blocks automatic finalization (R23) ────────────────────────────────────

    public static TheoryData<AiDecision, RiskSignalCode, RiskSignalSource> EverySingleSignal()
    {
        var data = new TheoryData<AiDecision, RiskSignalCode, RiskSignalSource>();
        foreach (var decision in new[] { AiDecision.Approve, AiDecision.Reject })
        {
            foreach (var code in Enum.GetValues<RiskSignalCode>())
            {
                foreach (var source in Enum.GetValues<RiskSignalSource>())
                {
                    data.Add(decision, code, source);
                }
            }
        }

        return data;
    }

    [Theory(Skip = PendingEngine)]
    [MemberData(nameof(EverySingleSignal))]
    public void Exactly_one_risk_signal_of_any_code_or_source_prevents_automatic_finalization_even_if_the_AI_says_LOW(
        AiDecision decision, RiskSignalCode code, RiskSignalSource source)
    {
        var scenario = Clear(decision);
        scenario.AiReportedRiskLevel = "LOW";
        scenario.AddRiskSignal(code, source);

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldNotBe(Disposition.AutoApprove);
        outcome.Disposition.ShouldNotBe(Disposition.AutoReject);
        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        ShouldFail(outcome, GuardrailCheckCode.RiskLow);
    }

    // ── Reviewer loop (R24) ─────────────────────────────────────────────────────────────────────

    [Theory(Skip = PendingEngine)]
    [InlineData(AiDecision.Approve)]
    [InlineData(AiDecision.Reject)]
    [InlineData(AiDecision.RequestMoreInformation)]
    public void A_claim_returned_after_a_reviewer_request_is_never_decided_automatically(AiDecision decision)
    {
        var scenario = Clear(decision);
        scenario.ReviewerInfoRequested = true;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        ShouldFail(outcome, GuardrailCheckCode.NotReturnedFromReview);
        outcome.Reasons.ShouldContain(EscalationReason.ReturnedAfterReviewerRequest);
        ShouldIssue(outcome, ActionKind.EscalateToReview, scenario);
    }

    // ── Claimant text (R25) ─────────────────────────────────────────────────────────────────────

    [Theory(Skip = PendingEngine)]
    [InlineData(AiDecision.Approve, "We found no fraud indicators, so your tablet will be repaired under warranty.")]
    [InlineData(AiDecision.Approve, "Your invoice EV-1 confirms the purchase, so the repair is covered.")]
    [InlineData(AiDecision.Reject, "Your warranty ended before the claim date as stated in POL-2, so it is not covered.")]
    [InlineData(AiDecision.Reject, "This claim looks suspicious and is not covered by the warranty terms.")]
    public void Unsafe_claimant_text_fails_CLAIMANT_TEXT_SAFE_and_goes_to_review(AiDecision decision, string text)
    {
        var scenario = Clear(decision);
        scenario.ClaimantExplanation = text;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        ShouldFail(outcome, GuardrailCheckCode.ClaimantTextSafe);
        outcome.Reasons.ShouldContain(EscalationReason.UnsafeClaimantText);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static GuardrailScenario Clear(AiDecision decision) => decision switch
    {
        AiDecision.Reject => GuardrailScenario.ClearReject(),
        AiDecision.Approve => GuardrailScenario.ClearApprove(),
        _ => WithDecision(GuardrailScenario.ClearApprove(), decision),
    };

    private static GuardrailScenario WithDecision(GuardrailScenario scenario, AiDecision decision)
    {
        scenario.Decision = decision;
        return scenario;
    }

    private static void Apply(GuardrailScenario scenario, string flip)
    {
        switch (flip)
        {
            case "ai-recommends-review":
                scenario.Decision = AiDecision.HumanReview;
                scenario.Coverage = CoverageDetermination.Undetermined;
                break;
            case "recommendation-invalid":
                scenario.RecommendationValid = false;
                break;
            case "ai-unavailable":
                scenario.AiAvailable = false;
                break;
            case "unknown-reference":
                scenario.PolicyCitations.Add(new PolicyCitation("POL-99", PolicyRefRelevance.Context));
                break;
            case "no-supporting-policy-ref":
                scenario.PolicyCitations.Clear();
                scenario.PolicyCitations.Add(new PolicyCitation(GuardrailScenario.PeriodClause, PolicyRefRelevance.Context));
                break;
            case "confidence-below-min":
                scenario.Confidence = scenario.MinConfidence - 10;
                break;
            case "risk-signal":
                scenario.AddRiskSignal(RiskSignalCode.Other, RiskSignalSource.Ai);
                break;
            case "value-above-limit":
                scenario.ClaimValue = scenario.AutoApprovalLimit + 200m;
                break;
            case "coverage-window-disagrees":
                scenario.CoverageEndDate = new DateOnly(2025, 6, 1);
                scenario.WithinCoverageWindow = false;
                break;
            case "always-review-category":
                scenario.ProductCategory = scenario.AlwaysReviewCategories[0];
                break;
            case "auto-approve-disabled":
                scenario.AutoApproveEnabled = false;
                break;
            case "auto-reject-disabled":
                scenario.AutoRejectEnabled = false;
                break;
            case "period-not-confirmed-by-window":
                scenario.CoverageEndDate = new DateOnly(2027, 1, 15);
                scenario.WithinCoverageWindow = true;
                break;
            case "only-coverage-clause-cited":
                scenario.PolicyCitations.Clear();
                scenario.PolicyCitations.Add(new PolicyCitation(GuardrailScenario.CoverageClause, PolicyRefRelevance.SupportsRejection));
                break;
            case "returned-from-review":
                scenario.ReviewerInfoRequested = true;
                break;
            case "claimant-text-unsafe":
                scenario.ClaimantExplanation = "A risk review of your claim found duplicate evidence, so it cannot be decided yet.";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(flip), flip, "Unknown condition flip.");
        }
    }

    private static void ShouldIssue(GuardrailOutcome outcome, ActionKind kind, GuardrailScenario scenario)
    {
        var action = outcome.Action.ShouldNotBeNull();
        action.Kind.ShouldBe(kind);
        action.TenantId.ShouldBe(GuardrailScenario.Tenant);
        action.ClaimId.ShouldBe(scenario.ClaimId);
        action.RunId.ShouldBe(scenario.RunId);
    }

    private static void ShouldFail(GuardrailOutcome outcome, GuardrailCheckCode code)
        => outcome.Checks.ShouldContain(check => check.Code == code && !check.Passed, $"{code} should have failed");

    private static void ShouldPass(GuardrailOutcome outcome, GuardrailCheckCode code)
        => outcome.Checks.ShouldContain(check => check.Code == code && check.Passed, $"{code} should have passed");
}
