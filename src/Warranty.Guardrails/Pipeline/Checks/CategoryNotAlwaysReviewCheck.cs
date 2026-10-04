using Warranty.Domain.Claims;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>CATEGORY_NOT_ALWAYS_REVIEW</c>: the product category is not one the tenant always routes to a human
/// (FR-026 – FR-028). An unknown category fails closed. Evaluated on every path.
/// </summary>
internal sealed class CategoryNotAlwaysReviewCheck : IGuardrailCheck
{
    public GuardrailCheckCode Code => GuardrailCheckCode.CategoryNotAlwaysReview;

    public string Stage => CheckStage.BusinessRules;

    public CheckResult Evaluate(GuardrailContext context)
    {
        var settings = context.Input.Settings;
        var expected = $"category not in [{string.Join(", ", settings.AlwaysReviewCategories)}]";
        var category = context.Input.Case.ProductCategory;
        if (string.IsNullOrWhiteSpace(category))
        {
            return CheckResult.Escalate(
                expected,
                "unknown",
                "The product category is unknown because the product is not in the tenant's catalog.",
                EscalationReason.ProductNotInCatalog);
        }

        return settings.AlwaysRequiresReview(category)
            ? CheckResult.Escalate(
                expected, category, "The tenant always routes this product category to a human.", EscalationReason.AlwaysReviewCategory)
            : CheckResult.Pass(expected, category, "The product category does not require a human decision.");
    }
}
