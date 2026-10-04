using Warranty.Domain.Claims;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>NO_CONFLICTS</c>: no claim-vs-evidence consistency check failed (FR-016). A field the evidence does
/// not show is not a conflict (research R27); the checks themselves are computed with
/// <c>EvidenceMatchRules</c>. Further conflict rules are added by T096.
/// </summary>
internal sealed class NoConflictsCheck : IGuardrailCheck
{
    private const string Expected = "claim and evidence consistent";

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

        var conflicts = evidence.ConsistencyChecks
            .Where(check => !check.Match && check.ClaimValue is not null && check.EvidenceValue is not null)
            .Select(check => $"{check.Field}: claim '{check.ClaimValue}' vs evidence '{check.EvidenceValue}'")
            .ToArray();
        if (conflicts.Length > 0)
        {
            return CheckResult.Escalate(
                Expected, Describe.List(conflicts), "The evidence conflicts with the claim.", EscalationReason.EvidenceConflict);
        }

        var compared = evidence.ConsistencyChecks.Select(check => check.Field).Distinct(StringComparer.Ordinal);
        return CheckResult.Pass(Expected, $"consistent: {Describe.List(compared)}", "No conflicting evidence.");
    }
}
