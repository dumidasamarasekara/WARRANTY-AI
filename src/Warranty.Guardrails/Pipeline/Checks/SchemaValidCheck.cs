using Warranty.Domain.Claims;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>SCHEMA_VALID</c>: the recommendation exists (FR-031), passed schema validation (FR-023) and is
/// complete — a <c>REQUEST_MORE_INFORMATION</c> must name the items it needs (FR-029).
/// </summary>
internal sealed class SchemaValidCheck : IGuardrailCheck
{
    private const string Expected = "a valid, complete AI recommendation";

    public GuardrailCheckCode Code => GuardrailCheckCode.SchemaValid;

    public string Stage => CheckStage.Schema;

    public CheckResult Evaluate(GuardrailContext context)
    {
        if (context.IsShortCircuit)
        {
            return CheckResult.NotApplicable(Expected, "The AI steps did not run: intake found missing items.");
        }

        var recommendation = context.Input.Recommendation;
        if (recommendation is null)
        {
            return CheckResult.Escalate(
                Expected, "no recommendation", "AI analysis could not be completed.", EscalationReason.AiUnavailable);
        }

        if (!recommendation.IsValid)
        {
            return CheckResult.Escalate(
                Expected,
                $"invalid: {Describe.List(recommendation.ValidationErrors)}",
                "The recommendation does not conform to the decision schema.",
                EscalationReason.InvalidRecommendation);
        }

        if (context.ValidRecommendation is not { } valid)
        {
            return CheckResult.Escalate(
                Expected,
                "decision, coverage or confidence missing",
                "The recommendation is incomplete.",
                EscalationReason.InvalidRecommendation);
        }

        var decision = Describe.Wire(valid.Decision!.Value);
        if (valid.Decision == AiDecision.RequestMoreInformation && valid.MissingInformation.Count == 0)
        {
            return CheckResult.Escalate(
                Expected,
                $"{decision} without any missing item",
                "A request for more information must name the items needed.",
                EscalationReason.InvalidRecommendation);
        }

        return CheckResult.Pass(Expected, $"valid {decision}", "The recommendation is valid.");
    }
}
