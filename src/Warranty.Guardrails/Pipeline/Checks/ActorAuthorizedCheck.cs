using Warranty.Domain.Claims;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>ACTOR_AUTHORIZED</c>: the evaluation is performed by the adjudication automation, the only principal
/// allowed to receive an automatically issued action (research R13 stage 6). Tenant consistency of every
/// input is verified before the checks run (the engine throws on a mismatch). Evaluated on every path.
/// </summary>
/// <remarks>
/// data-model.md has no escalation reason for an unauthorized actor; the failure is reported as
/// <see cref="EscalationReason.AiDeterministicDisagreement"/> and the check itself names the actor.
/// </remarks>
internal sealed class ActorAuthorizedCheck : IGuardrailCheck
{
    public GuardrailCheckCode Code => GuardrailCheckCode.ActorAuthorized;

    public string Stage => CheckStage.Authorization;

    public CheckResult Evaluate(GuardrailContext context)
    {
        var actor = context.Input.Actor;
        var tenant = context.Input.Case.TenantId;
        const string expected = "the adjudication automation acting for the claim's tenant";
        if (actor is { IsAutomation: true } && !string.IsNullOrWhiteSpace(actor.Subject))
        {
            return CheckResult.Pass(expected, $"automation '{actor.Subject}' for tenant {tenant}", "The actor may perform automatic actions.");
        }

        return CheckResult.Escalate(
            expected,
            $"'{actor?.Subject}' (automation: {Describe.Bool(actor?.IsAutomation == true)}) for tenant {tenant}",
            "The actor may not perform automatic actions.",
            EscalationReason.AiDeterministicDisagreement);
    }
}
