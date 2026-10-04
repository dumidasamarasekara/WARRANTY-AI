using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>POLICY_APPLICABLE</c>: exactly one policy version applies on the purchase date (clarification
/// Q1); none (FR-014) or more than one escalates.
/// </summary>
internal sealed class PolicyApplicableCheck : IGuardrailCheck
{
    private const string Expected = "exactly one applicable policy version";

    public GuardrailCheckCode Code => GuardrailCheckCode.PolicyApplicable;

    public string Stage => CheckStage.BusinessRules;

    public CheckResult Evaluate(GuardrailContext context)
    {
        if (context.IsShortCircuit)
        {
            return CheckResult.NotApplicable(Expected, "The policy step did not run: intake found missing items.");
        }

        var policy = context.Input.Policy;
        if (policy is null)
        {
            return CheckResult.Escalate(
                Expected, "no policy result", "The policy step did not complete.", EscalationReason.AiUnavailable);
        }

        return policy.VersionOutcome switch
        {
            PolicyVersionOutcome.Ok when policy.Version is { } version => CheckResult.Pass(
                Expected, $"version {Describe.Number(version.Version)} ({version.Id})", "One policy version applies."),
            PolicyVersionOutcome.AmbiguousPolicyVersion => CheckResult.Escalate(
                Expected, "more than one applicable version", "More than one policy version could apply.", EscalationReason.AmbiguousPolicy),
            _ => CheckResult.Escalate(
                Expected, "no applicable version", "No applicable policy version was found.", EscalationReason.NoApplicablePolicy),
        };
    }
}
