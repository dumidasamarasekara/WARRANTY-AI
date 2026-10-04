using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>RISK_LOW</c>: passes only when the computed risk assessment lists no signal of any kind (FR-017,
/// research R23); the model's own risk level is never used. On the full path the assessment must cover
/// the full run (stage <c>Full</c>), so AI-reported signals are included. Evaluated on every path.
/// </summary>
internal sealed class RiskLowCheck : IGuardrailCheck
{
    private const string Expected = "no risk signal";

    public GuardrailCheckCode Code => GuardrailCheckCode.RiskLow;

    public string Stage => CheckStage.Thresholds;

    public CheckResult Evaluate(GuardrailContext context)
    {
        var risk = context.Input.Risk;
        if (risk is null)
        {
            return CheckResult.Escalate(Expected, "not assessed", "Risk could not be assessed.", EscalationReason.AiUnavailable);
        }

        if (!context.IsShortCircuit && risk.Stage != RiskAssessmentStage.Full)
        {
            return CheckResult.Escalate(
                Expected,
                $"assessed at stage {risk.Stage} only",
                "Risk was not assessed for the full run.",
                EscalationReason.AiUnavailable);
        }

        if (risk.Signals.Count == 0)
        {
            return CheckResult.Pass(Expected, "no signals (level Low)", "No risk signal is present.");
        }

        var signals = risk.Signals.Select(signal => $"{Describe.Wire(signal.Code)} ({Describe.Wire(signal.Source)})");
        return CheckResult.Escalate(
            Expected,
            $"level {risk.Level}, score {Describe.Number(risk.Score)}: {Describe.List(signals)}",
            "Risk signals are present, so risk is not low.",
            ReasonFor(risk.Level));
    }

    /// <summary>Any signal makes risk at least Medium (R23).</summary>
    internal static EscalationReason ReasonFor(RiskLevel level)
        => level == RiskLevel.High ? EscalationReason.RiskHigh : EscalationReason.RiskMedium;
}
