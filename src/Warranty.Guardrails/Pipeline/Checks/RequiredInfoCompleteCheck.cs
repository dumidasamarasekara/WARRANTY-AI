using Warranty.Domain.Claims;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>REQUIRED_INFO_COMPLETE</c>: every intake validation check passed and no item is missing according
/// to intake, evidence analysis or the recommendation (FR-009, FR-010). Missing items lead to a request
/// for information when nothing escalates; information that is incomplete without any item that can be
/// requested escalates (fail closed).
/// </summary>
internal sealed class RequiredInfoCompleteCheck : IGuardrailCheck
{
    private const string Expected = "all required information present and valid";

    public GuardrailCheckCode Code => GuardrailCheckCode.RequiredInfoComplete;

    public string Stage => CheckStage.BusinessRules;

    public CheckResult Evaluate(GuardrailContext context)
    {
        if (context.RequiredInfoComplete)
        {
            return CheckResult.Pass(Expected, "complete", "All required information is present.");
        }

        if (context.RequestedItems.Count > 0)
        {
            return CheckResult.InformationMissing(
                Expected,
                $"missing: {Describe.List(context.RequestedItems.Select(item => item.Item))}",
                "Information is missing; it can be requested from the submitter.");
        }

        var failedValidation = context.Input.Intake.Validation.Where(check => !check.Passed).Select(check => check.Check);
        return CheckResult.Escalate(
            Expected,
            $"failed validation without a requestable item: {Describe.List(failedValidation)}",
            "Information is incomplete but no item can be requested, so the analysis cannot be completed.",
            EscalationReason.AiUnavailable);
    }
}
