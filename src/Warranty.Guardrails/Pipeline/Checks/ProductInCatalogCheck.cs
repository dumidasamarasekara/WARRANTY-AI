using Warranty.Domain.Claims;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary><c>PRODUCT_IN_CATALOG</c>: the product/serial pair is in the tenant's catalog (FR-003, FR-025).</summary>
internal sealed class ProductInCatalogCheck : IGuardrailCheck
{
    private const string Expected = "product and serial in the tenant's catalog";

    public GuardrailCheckCode Code => GuardrailCheckCode.ProductInCatalog;

    public string Stage => CheckStage.BusinessRules;

    public CheckResult Evaluate(GuardrailContext context)
        => context.Input.Case.ProductInCatalog
            ? CheckResult.Pass(Expected, "in catalog", "The product is in the tenant's catalog.")
            : CheckResult.Escalate(
                Expected, "not in catalog", "The product or serial is not in the tenant's catalog.", EscalationReason.ProductNotInCatalog);
}
