using Warranty.Domain.Claims;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>AUTO_DECISION_ENABLED</c>: the tenant allows the automatic decision the AI recommends —
/// <c>auto_approve_enabled</c> for <c>APPROVE</c>, <c>auto_reject_enabled</c> for <c>REJECT</c>
/// (clarification Q2). Each switch governs only its own decision; requests for information are not
/// governed by either. A failure prevents automatic finalization.
/// </summary>
/// <remarks>
/// data-model.md has no escalation reason for a disabled switch; the failure is reported as
/// <see cref="EscalationReason.AiDeterministicDisagreement"/> (the recommended automatic decision
/// conflicts with this independent check), and the check itself names the switch.
/// </remarks>
internal sealed class AutoDecisionEnabledCheck : IGuardrailCheck
{
    public GuardrailCheckCode Code => GuardrailCheckCode.AutoDecisionEnabled;

    public string Stage => CheckStage.Authorization;

    public CheckResult Evaluate(GuardrailContext context)
    {
        const string expected = "automatic decision enabled for the tenant";
        if (context.IsShortCircuit)
        {
            return CheckResult.NotApplicable(expected, "Requests for information are not governed by the automatic decision switches.");
        }

        if (context.ValidRecommendation is null)
        {
            return CheckResult.NoValidRecommendation(context, expected);
        }

        var settings = context.Input.Settings;
        return context.Decision switch
        {
            AiDecision.Approve => Evaluate("auto_approve_enabled", settings.AutoApproveEnabled, "approval"),
            AiDecision.Reject => Evaluate("auto_reject_enabled", settings.AutoRejectEnabled, "rejection"),
            _ => CheckResult.NotApplicable(expected, "Only an APPROVE or REJECT is decided automatically."),
        };
    }

    private static CheckResult Evaluate(string setting, bool enabled, string decision)
    {
        var expected = $"{setting} = true";
        var actual = $"{setting} = {Describe.Bool(enabled)}";
        return enabled
            ? CheckResult.Pass(expected, actual, $"Automatic {decision} is enabled for the tenant.")
            : CheckResult.BlockFinalization(
                expected, actual, $"Automatic {decision} is disabled for the tenant.", EscalationReason.AiDeterministicDisagreement);
    }
}
