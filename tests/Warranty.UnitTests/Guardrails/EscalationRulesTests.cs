using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Guardrails;

namespace Warranty.UnitTests.Guardrails;

/// <summary>
/// Escalation rules of the guardrail engine (spec FR-028, FR-010, FR-034, contracts/agents-and-tools.md
/// disposition table, research R23 – R25): each trigger on its own, applied to an otherwise clear-cut
/// claim, yields <see cref="Disposition.HumanReview"/> with exactly the matching <see cref="EscalationReason"/>.
/// </summary>
public sealed class EscalationRulesTests
{
    // ── Each trigger alone ──────────────────────────────────────────────────────────────────────

    public static TheoryData<string, AiDecision, GuardrailCheckCode?, EscalationReason> SingleTriggers()
    {
        var data = new TheoryData<string, AiDecision, GuardrailCheckCode?, EscalationReason>();
        foreach (var decision in new[] { AiDecision.Approve, AiDecision.Reject })
        {
            data.Add("value-above-limit", decision, GuardrailCheckCode.ClaimValueWithinLimit, EscalationReason.ValueAboveLimit);
            data.Add("confidence-below-min", decision, GuardrailCheckCode.ConfidenceAtOrAboveMin, EscalationReason.ConfidenceBelowMin);
            data.Add("risk-medium", decision, GuardrailCheckCode.RiskLow, EscalationReason.RiskMedium);
            data.Add("risk-high", decision, GuardrailCheckCode.RiskLow, EscalationReason.RiskHigh);
            data.Add("always-review-category", decision, GuardrailCheckCode.CategoryNotAlwaysReview, EscalationReason.AlwaysReviewCategory);
            data.Add("no-applicable-policy", decision, GuardrailCheckCode.PolicyApplicable, EscalationReason.NoApplicablePolicy);
            data.Add("ambiguous-policy-version", decision, GuardrailCheckCode.PolicyApplicable, EscalationReason.AmbiguousPolicy);
            data.Add("coverage-undetermined", decision, GuardrailCheckCode.CoverageWindowAgrees, EscalationReason.AmbiguousPolicy);
            data.Add("returned-from-review", decision, GuardrailCheckCode.NotReturnedFromReview, EscalationReason.ReturnedAfterReviewerRequest);
            data.Add("unsafe-claimant-text", decision, GuardrailCheckCode.ClaimantTextSafe, EscalationReason.UnsafeClaimantText);
        }

        data.Add("ai-recommends-review", AiDecision.Approve, null, EscalationReason.AiRecommendsReview);
        return data;
    }

    [Theory]
    [MemberData(nameof(SingleTriggers))]
    public void Each_trigger_alone_escalates_with_its_reason(
        string trigger, AiDecision decision, GuardrailCheckCode? failedCheck, EscalationReason reason)
    {
        var scenario = Clear(decision);
        Apply(scenario, trigger);

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview, trigger);
        if (trigger is "no-applicable-policy" or "ambiguous-policy-version")
        {
            // Without an applicable version the coverage window and the clause grounding cannot be
            // established either, so those checks add their own reasons after POLICY_APPLICABLE's.
            outcome.Reasons[0].ShouldBe(reason, trigger);
        }
        else
        {
            outcome.Reasons.ShouldBe([reason], trigger);
        }

        ShouldIssue(outcome, ActionKind.EscalateToReview, scenario);
        outcome.Action!.RequestedItems.ShouldBeEmpty();
        if (failedCheck is { } code)
        {
            ShouldFail(outcome, code);
        }
    }

    [Fact]
    public void The_AI_recommending_review_escalates_even_when_every_check_passes()
    {
        var scenario = GuardrailScenario.ClearApprove();
        scenario.Decision = AiDecision.HumanReview;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        outcome.Reasons.ShouldBe([EscalationReason.AiRecommendsReview]);
        ShouldIssue(outcome, ActionKind.EscalateToReview, scenario);
    }

    // ── Returned after a reviewer's request (R24) ───────────────────────────────────────────────

    [Fact]
    public void A_perfect_AI_approval_after_a_reviewer_request_escalates_as_returned_after_reviewer_request()
    {
        var scenario = GuardrailScenario.ClearApprove();
        scenario.Evaluate().Disposition.ShouldBe(Disposition.AutoApprove);
        scenario.ReviewerInfoRequested = true;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        outcome.Reasons.ShouldBe([EscalationReason.ReturnedAfterReviewerRequest]);
        ShouldFail(outcome, GuardrailCheckCode.NotReturnedFromReview);
        ShouldIssue(outcome, ActionKind.EscalateToReview, scenario);
    }

    [Fact]
    public void An_AI_request_for_more_information_after_a_reviewer_request_escalates_instead_of_asking_the_submitter()
    {
        var scenario = NeedsInformation();
        scenario.ReviewerInfoRequested = true;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        outcome.Reasons.ShouldBe([EscalationReason.ReturnedAfterReviewerRequest]);
        ShouldFail(outcome, GuardrailCheckCode.NotReturnedFromReview);
        ShouldIssue(outcome, ActionKind.EscalateToReview, scenario);
        outcome.Action!.RequestedItems.ShouldBeEmpty();
    }

    // ── Automatic information requests limit (FR-010, R24) ──────────────────────────────────────

    [Fact]
    public void Information_still_missing_after_two_automatic_requests_escalates()
    {
        var scenario = NeedsInformation();
        scenario.AutoInfoRequestCount = GuardrailEngine.MaxAutomaticInformationRequests;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.HumanReview);
        outcome.Reasons.ShouldBe([EscalationReason.InfoIncompleteAfterTwoRequests]);
        ShouldFail(outcome, GuardrailCheckCode.AutoInfoRequestsWithinLimit);
        ShouldIssue(outcome, ActionKind.EscalateToReview, scenario);
    }

    [Fact]
    public void Information_missing_after_one_automatic_request_is_requested_again()
    {
        var scenario = NeedsInformation();
        scenario.AutoInfoRequestCount = 1;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(Disposition.RequestInformation);
        outcome.Reasons.ShouldBeEmpty();
        ShouldPass(outcome, GuardrailCheckCode.AutoInfoRequestsWithinLimit);
        ShouldIssue(outcome, ActionKind.RequestInformation, scenario);
        outcome.Action!.RequestedItems.Select(item => item.Item).ShouldBe([RequestedItemCodes.PhotoOfDamage]);
    }

    [Theory]
    [InlineData(1, Disposition.RequestInformation)]
    [InlineData(2, Disposition.HumanReview)]
    public void Intake_missing_items_follow_the_same_request_limit(int autoInfoRequestCount, Disposition expected)
    {
        var scenario = GuardrailScenario.ClearApprove();
        scenario.AiAvailable = false;
        scenario.IntakeMissingItems.Add(new RequestedItem(RequestedItemCodes.Invoice, "No invoice was uploaded."));
        scenario.AutoInfoRequestCount = autoInfoRequestCount;

        var outcome = scenario.Evaluate();

        outcome.Disposition.ShouldBe(expected);
        if (expected == Disposition.HumanReview)
        {
            outcome.Reasons.ShouldBe([EscalationReason.InfoIncompleteAfterTwoRequests]);
            ShouldIssue(outcome, ActionKind.EscalateToReview, scenario);
        }
        else
        {
            outcome.Reasons.ShouldBeEmpty();
            ShouldIssue(outcome, ActionKind.RequestInformation, scenario);
            outcome.Action!.RequestedItems.Select(item => item.Item).ShouldBe([RequestedItemCodes.Invoice]);
        }
    }

    // ── Labels ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_escalation_reason_has_a_readable_label()
    {
        foreach (var reason in Enum.GetValues<EscalationReason>())
        {
            reason.Label().ShouldNotBeNullOrWhiteSpace(reason.ToString());
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static GuardrailScenario Clear(AiDecision decision)
        => decision == AiDecision.Reject ? GuardrailScenario.ClearReject() : GuardrailScenario.ClearApprove();

    /// <summary>An otherwise clear claim on which the AI asks for a photo of the damage (FR-010).</summary>
    private static GuardrailScenario NeedsInformation()
    {
        var scenario = GuardrailScenario.ClearApprove();
        scenario.Decision = AiDecision.RequestMoreInformation;
        scenario.AiMissingInformation.Add(new RequestedItem(RequestedItemCodes.PhotoOfDamage, "No photo shows the reported defect."));
        return scenario;
    }

    private static void Apply(GuardrailScenario scenario, string trigger)
    {
        switch (trigger)
        {
            case "value-above-limit":
                scenario.ClaimValue = scenario.AutoApprovalLimit + 0.01m;
                break;
            case "confidence-below-min":
                scenario.Confidence = scenario.MinConfidence - 1;
                break;
            case "risk-medium":
                scenario.AddRiskSignal(RiskSignalCode.Other, RiskSignalSource.Deterministic);
                scenario.SignalledRiskLevel = RiskLevel.Medium;
                break;
            case "risk-high":
                scenario.AddRiskSignal(RiskSignalCode.Other, RiskSignalSource.Deterministic);
                scenario.SignalledRiskLevel = RiskLevel.High;
                break;
            case "always-review-category":
                scenario.ProductCategory = scenario.AlwaysReviewCategories[0];
                break;
            case "no-applicable-policy":
                scenario.VersionOutcome = PolicyVersionOutcome.NoApplicablePolicy;
                break;
            case "ambiguous-policy-version":
                scenario.VersionOutcome = PolicyVersionOutcome.AmbiguousPolicyVersion;
                break;
            case "coverage-undetermined":
                // The Policy agent's ambiguity flag reaches the guardrails as an UNDETERMINED coverage reading.
                scenario.Coverage = CoverageDetermination.Undetermined;
                break;
            case "returned-from-review":
                scenario.ReviewerInfoRequested = true;
                break;
            case "unsafe-claimant-text":
                scenario.ClaimantExplanation = "We checked your claim for fraud and found nothing, so it is decided as stated.";
                break;
            case "ai-recommends-review":
                scenario.Decision = AiDecision.HumanReview;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unknown escalation trigger.");
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
