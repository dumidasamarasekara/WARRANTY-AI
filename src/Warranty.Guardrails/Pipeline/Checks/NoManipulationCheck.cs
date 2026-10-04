using Warranty.Domain.Claims;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>NO_MANIPULATION</c>: no <c>MANIPULATION_ATTEMPT</c> signal and no manipulation flagged by the AI
/// (FR-019, research R14). Evaluated on every path; the deterministic detector runs at intake.
/// </summary>
internal sealed class NoManipulationCheck : IGuardrailCheck
{
    private const string Expected = "no attempt to influence the decision";

    public GuardrailCheckCode Code => GuardrailCheckCode.NoManipulation;

    public string Stage => CheckStage.Conflicts;

    public CheckResult Evaluate(GuardrailContext context)
    {
        var risk = context.Input.Risk;
        if (risk is null)
        {
            return CheckResult.Escalate(Expected, "not assessed", "Risk could not be assessed.", EscalationReason.AiUnavailable);
        }

        var signal = risk.Signals.Any(s => s.Code == RiskSignalCode.ManipulationAttempt);
        var flagged = context.Input.Recommendation?.ManipulationDetected == true;
        if (!signal && !flagged)
        {
            return CheckResult.Pass(Expected, "none detected", "No manipulation attempt was detected.");
        }

        var actual = (signal, flagged) switch
        {
            (true, true) => "MANIPULATION_ATTEMPT signal; flagged by the AI",
            (true, false) => "MANIPULATION_ATTEMPT signal",
            _ => "flagged by the AI",
        };
        return CheckResult.Escalate(
            Expected, actual, "Submitted content attempted to influence the decision.", RiskLowCheck.ReasonFor(risk.Level));
    }
}
