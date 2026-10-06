using Warranty.Domain.Claims;
using Warranty.Guardrails.Rules;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>NO_CONFLICTS</c>: no claim-vs-evidence consistency check failed (FR-016). A field the evidence does
/// not show is not a conflict (research R27); the checks themselves are computed with
/// <c>EvidenceMatchRules</c>. An AI <c>APPROVE</c> against a failed check is called out, and an AI decision on
/// coverage for a product/serial not in the tenant's catalog is a conflict too (<see cref="ConflictRules"/>).
/// </summary>
internal sealed class NoConflictsCheck : IGuardrailCheck
{
    private const string Expected = "claim and evidence consistent; AI decision consistent with the catalog";

    public GuardrailCheckCode Code => GuardrailCheckCode.NoConflicts;

    public string Stage => CheckStage.Conflicts;

    public CheckResult Evaluate(GuardrailContext context)
    {
        if (context.IsShortCircuit)
        {
            return CheckResult.NotApplicable(Expected, "Evidence analysis did not run: intake found missing items.");
        }

        var evidence = context.Input.Evidence;
        if (evidence is null)
        {
            return CheckResult.Escalate(
                Expected, "no evidence result", "Evidence analysis did not complete.", EscalationReason.AiUnavailable);
        }

        var conflicts = ConflictRules.EvidenceConflicts(evidence.ConsistencyChecks);
        if (conflicts.Count > 0)
        {
            return CheckResult.Escalate(
                Expected,
                Describe.List(conflicts),
                ConflictRules.EvidenceConflictMessage(context.Decision),
                EscalationReason.EvidenceConflict);
        }

        if (context.ValidRecommendation is { } recommendation
            && ConflictRules.CatalogAssumption(context.Input.Case.ProductInCatalog, recommendation.Decision!.Value, recommendation.Coverage!.Value)
                is { } catalogConflict)
        {
            return CheckResult.Escalate(
                Expected,
                $"AI {Describe.Wire(recommendation.Decision.Value)}/{Describe.Wire(recommendation.Coverage.Value)}; product not in catalog",
                catalogConflict,
                EscalationReason.AiDeterministicDisagreement);
        }

        var compared = evidence.ConsistencyChecks.Select(check => check.Field).Distinct(StringComparer.Ordinal);
        return CheckResult.Pass(Expected, $"consistent: {Describe.List(compared)}", "No conflicting evidence.");
    }
}
