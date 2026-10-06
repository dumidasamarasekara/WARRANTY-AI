using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;

namespace Warranty.Guardrails.Rules;

/// <summary>
/// Conflicts between the AI recommendation and the deterministic facts, and between the claim and its
/// evidence (FR-016, FR-025, FR-028, US4 scenario 1), used by <c>COVERAGE_WINDOW_AGREES</c> and
/// <c>NO_CONFLICTS</c>. Pure; reads structured facts only. Each rule returns a staff-facing explanation,
/// or null when there is no conflict. Exclusion grounding is not a conflict rule: it is
/// <c>GROUNDED_IN_CLAUSE</c> (research R26).
/// </summary>
internal static class ConflictRules
{
    /// <summary>The wording a failed coverage-window comparison is reported with (spec US4 scenario 1).</summary>
    public const string CoveragePeriodConflict = "AI recommendation conflicts with coverage-period check";

    /// <summary>
    /// AI coverage reading vs the deterministic coverage window: an <c>APPROVE</c> (or any <c>COVERED</c>
    /// reading) after the window ended, or a <c>REJECT</c> resting on a period clause while the claim date
    /// is within the window.
    /// </summary>
    public static string? CoverageWindow(
        AiDecision decision, CoverageDetermination coverage, bool withinWindow, bool rejectsOnPeriodClause)
    {
        var explanation = (decision, coverage, withinWindow) switch
        {
            (AiDecision.Approve, CoverageDetermination.Covered, false) =>
                "the AI approves, but the claim date is after the coverage window.",
            (AiDecision.Reject, CoverageDetermination.NotCovered, true) when rejectsOnPeriodClause =>
                "the AI rejects on the coverage period, but the claim date is within the coverage window.",
            (not AiDecision.Approve and not AiDecision.Reject, CoverageDetermination.Covered, false) =>
                "the AI reads the claim as covered, but the coverage window has ended.",
            _ => null,
        };

        return explanation is null ? null : $"{CoveragePeriodConflict}: {explanation}";
    }

    /// <summary>
    /// The failed claim-vs-evidence consistency checks (computed with <see cref="EvidenceMatchRules"/>), as
    /// <c>field: claim 'x' vs evidence 'y'</c>; a field the evidence does not show is not a conflict (research R27).
    /// </summary>
    public static IReadOnlyList<string> EvidenceConflicts(IEnumerable<ConsistencyCheck> checks)
        => checks
            .Where(check => !check.Match && check.ClaimValue is not null && check.EvidenceValue is not null)
            .Select(check => $"{check.Field}: claim '{check.ClaimValue}' vs evidence '{check.EvidenceValue}'")
            .ToArray();

    /// <summary>The message for failed consistency checks; an AI <c>APPROVE</c> against them is called out.</summary>
    public static string EvidenceConflictMessage(AiDecision? decision)
        => decision == AiDecision.Approve
            ? "The AI approves, but the claim conflicts with the evidence (failed invoice/evidence consistency)."
            : "The evidence conflicts with the claim.";

    /// <summary>
    /// AI product/serial assumptions vs the catalog: an <c>APPROVE</c> or <c>REJECT</c> with a determined
    /// coverage reading for a product/serial that is not in the tenant's catalog assumes terms that do not apply.
    /// </summary>
    public static string? CatalogAssumption(bool productInCatalog, AiDecision decision, CoverageDetermination coverage)
        => !productInCatalog
            && decision is AiDecision.Approve or AiDecision.Reject
            && coverage != CoverageDetermination.Undetermined
                ? "The AI decides coverage for a product or serial that is not in the tenant's catalog."
                : null;
}
