using Warranty.Domain.Claims;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>CLAIM_VALUE_WITHIN_LIMIT</c>: the catalog claim value is at or below the tenant's auto-approval limit
/// (FR-026, clarification Q4). An unknown value (product not in the catalog) fails closed. Evaluated on
/// every path (FR-028 precedence over FR-010).
/// </summary>
internal sealed class ClaimValueWithinLimitCheck : IGuardrailCheck
{
    public GuardrailCheckCode Code => GuardrailCheckCode.ClaimValueWithinLimit;

    public string Stage => CheckStage.BusinessRules;

    public CheckResult Evaluate(GuardrailContext context)
    {
        var settings = context.Input.Settings;
        var expected = $"≤ {Describe.Money(settings.AutoApprovalLimit, settings.Currency)}";
        if (context.Input.Case.ClaimValue is not { } value)
        {
            return CheckResult.Escalate(
                expected,
                "unknown",
                "The claim value is unknown because the product is not in the tenant's catalog.",
                EscalationReason.ProductNotInCatalog);
        }

        var actual = Describe.Money(value, settings.Currency);
        return settings.IsAboveAutoApprovalLimit(value)
            ? CheckResult.Escalate(expected, actual, "The claim value is above the auto-approval limit.", EscalationReason.ValueAboveLimit)
            : CheckResult.Pass(expected, actual, "The claim value is within the auto-approval limit.");
    }
}
