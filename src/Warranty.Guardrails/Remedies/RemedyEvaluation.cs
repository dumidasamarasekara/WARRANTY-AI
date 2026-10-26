using Warranty.Domain.Fulfillment;

namespace Warranty.Guardrails.Remedies;

/// <summary>
/// Result of the deterministic remedy evaluation (002 plan.md "Remedy evaluation", research R2). The same
/// rules, scenario and facts always give the same result (FR-010).
/// </summary>
/// <param name="Scenario">The scenario that was evaluated.</param>
/// <param name="TableRow">
/// The remedy-table row the scenario reads (<c>coveredDefect</c>, <c>protectionPlan</c>,
/// <c>repairNotEconomical</c>, <c>repairFailed</c>, <c>replacementNotAvailable</c>), recorded with the decision even when the row is missing.
/// </param>
/// <param name="AllowedRemedies">The remedies the row permits; empty when the rules or the row are missing.</param>
/// <param name="SingleRemedy">The remedy when exactly one is allowed (the recommendation); null for a choice.</param>
/// <param name="NeedsReviewer">True when a reviewer must decide or confirm the remedy (FR-011, FR-012, FR-031, FR-032).</param>
/// <param name="PendingReason">Why a reviewer is needed; null when the remedy is set automatically or the repair continues.</param>
/// <param name="ContinueRepair">True when the repair goes on: failed attempts below the limit, or an economical estimate.</param>
/// <param name="EconomicLimitAmount">The economic limit in money (limit percent × claim value), when the version has one and the claim value is known.</param>
public sealed record RemedyEvaluation(
    RemedyScenario Scenario,
    string TableRow,
    IReadOnlyList<RemedyType> AllowedRemedies,
    RemedyType? SingleRemedy,
    bool NeedsReviewer,
    RemedyPendingReason? PendingReason,
    bool ContinueRepair,
    decimal? EconomicLimitAmount)
{
    /// <summary>The remedy the system sets without a reviewer: the single allowed remedy when no reviewer is needed.</summary>
    public RemedyType? AutomaticRemedy => NeedsReviewer || ContinueRepair ? null : SingleRemedy;
}
