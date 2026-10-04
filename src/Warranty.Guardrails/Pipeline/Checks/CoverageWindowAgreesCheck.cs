using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Rules;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>COVERAGE_WINDOW_AGREES</c>: the AI's coverage reading agrees with the deterministic coverage window
/// (FR-025, FR-026, FR-028). An <c>APPROVE</c> needs <c>COVERED</c> and a claim date inside the window.
/// A <c>REJECT</c> needs <c>NOT_COVERED</c>; inside the window it may rest on an exclusion (decided by
/// <c>GROUNDED_IN_CLAUSE</c>), but citing a period clause as its ground contradicts the window. An
/// undetermined coverage reading on an <c>APPROVE</c>/<c>REJECT</c> is ambiguous policy; a window that
/// cannot be determined escalates on every path that has a policy result.
/// </summary>
internal sealed class CoverageWindowAgreesCheck : IGuardrailCheck
{
    public GuardrailCheckCode Code => GuardrailCheckCode.CoverageWindowAgrees;

    public string Stage => CheckStage.BusinessRules;

    public CheckResult Evaluate(GuardrailContext context)
    {
        const string expected = "AI coverage reading agrees with the deterministic coverage window";
        if (context.IsShortCircuit)
        {
            return CheckResult.NotApplicable(expected, "The policy step did not run: intake found missing items.");
        }

        var window = context.Input.Policy?.CoverageWindow;
        if (context.WithinCoverage is not { } within || window?.CoverageEndDate is not { } end)
        {
            return window is { Outcome: CoverageWindowOutcome.Determined }
                ? CheckResult.Escalate(
                    expected,
                    "coverage window inconsistent with the claim date",
                    "The deterministic coverage window could not be confirmed.",
                    EscalationReason.AiDeterministicDisagreement)
                : CheckResult.Escalate(
                    expected,
                    "coverage window not determined",
                    "No applicable policy terms to compute the coverage window.",
                    EscalationReason.NoApplicablePolicy);
        }

        if (context.ValidRecommendation is not { } recommendation)
        {
            return CheckResult.NoValidRecommendation(context, expected);
        }

        var coverage = recommendation.Coverage!.Value;
        var actual = $"AI {Describe.Wire(context.Decision!.Value)}/{Describe.Wire(coverage)}; claim date "
            + $"{Describe.Date(context.Input.ClaimDate)} {(within ? "within" : "after")} coverage end {Describe.Date(end)}";

        return context.Decision switch
        {
            AiDecision.Approve => EvaluateApprove(expected, actual, coverage, within),
            AiDecision.Reject => EvaluateReject(context, expected, actual, coverage, within),
            _ => coverage == CoverageDetermination.Covered && !within
                ? Disagreement(expected, actual, "The AI reads the claim as covered, but the coverage window has ended.")
                : CheckResult.Pass(expected, actual, "The AI coverage reading does not contradict the coverage window."),
        };
    }

    private static CheckResult EvaluateApprove(string expected, string actual, CoverageDetermination coverage, bool within)
    {
        if (coverage == CoverageDetermination.Undetermined)
        {
            return Undetermined(expected, actual);
        }

        if (coverage != CoverageDetermination.Covered)
        {
            return Disagreement(expected, actual, "The AI approves a claim it reads as not covered.");
        }

        return within
            ? CheckResult.Pass(expected, actual, "The claim date is within the coverage window.")
            : Disagreement(expected, actual, "The AI approves, but the claim date is after the coverage window.");
    }

    private static CheckResult EvaluateReject(
        GuardrailContext context, string expected, string actual, CoverageDetermination coverage, bool within)
    {
        if (coverage == CoverageDetermination.Undetermined)
        {
            return Undetermined(expected, actual);
        }

        if (coverage != CoverageDetermination.NotCovered)
        {
            return Disagreement(expected, actual, "The AI rejects a claim it reads as covered.");
        }

        var citesExpiredPeriod = context.ValidRecommendation!.PolicyRefs.Any(citation =>
            citation.Relevance == PolicyRefRelevance.SupportsRejection
            && context.TryGetIssuedClause(citation.Ref, out var clause)
            && clause.ClauseType == ClauseType.Period);
        if (within && citesExpiredPeriod)
        {
            return Disagreement(
                expected, actual, "The AI rejects on the coverage period, but the claim date is within the coverage window.");
        }

        return CheckResult.Pass(expected, actual, "The AI coverage reading does not contradict the coverage window.");
    }

    private static CheckResult Undetermined(string expected, string actual)
        => CheckResult.Escalate(
            expected, actual, "The coverage determination is undetermined.", EscalationReason.AmbiguousPolicy);

    private static CheckResult Disagreement(string expected, string actual, string message)
        => CheckResult.Escalate(expected, actual, message, EscalationReason.AiDeterministicDisagreement);
}
