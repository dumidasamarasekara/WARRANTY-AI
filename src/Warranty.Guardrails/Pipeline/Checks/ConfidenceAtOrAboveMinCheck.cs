using Warranty.Domain.Claims;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary><c>CONFIDENCE_AT_OR_ABOVE_MIN</c>: the AI's confidence is at or above the tenant's minimum (FR-026 – FR-028).</summary>
internal sealed class ConfidenceAtOrAboveMinCheck : IGuardrailCheck
{
    public GuardrailCheckCode Code => GuardrailCheckCode.ConfidenceAtOrAboveMin;

    public string Stage => CheckStage.Thresholds;

    public CheckResult Evaluate(GuardrailContext context)
    {
        var settings = context.Input.Settings;
        var expected = $"≥ {Describe.Number(settings.MinConfidence)}";
        if (context.IsShortCircuit)
        {
            return CheckResult.NotApplicable(expected, "The AI steps did not run: intake found missing items.");
        }

        if (context.ValidRecommendation is not { Confidence: { } confidence })
        {
            return CheckResult.NoValidRecommendation(context, expected);
        }

        var actual = Describe.Number(confidence);
        return settings.IsBelowMinConfidence(confidence)
            ? CheckResult.Escalate(expected, actual, "The AI's confidence is below the tenant's minimum.", EscalationReason.ConfidenceBelowMin)
            : CheckResult.Pass(expected, actual, "The AI's confidence meets the tenant's minimum.");
    }
}
