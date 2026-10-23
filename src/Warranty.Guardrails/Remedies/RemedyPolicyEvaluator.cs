using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;

namespace Warranty.Guardrails.Remedies;

/// <summary>
/// The one deterministic remedy evaluator (002 research R2): a pure function of the policy version's remedy
/// rules, the scenario and the repair facts. Every company runs the same code; only the rules differ (FR-008).
/// </summary>
public sealed class RemedyPolicyEvaluator
{
    /// <param name="rules">The applicable policy version's remedy rules; null when the version has none.</param>
    /// <param name="scenario">The scenario to evaluate (research R13).</param>
    /// <param name="failedAttempts">Failed repair attempts so far (for <see cref="RemedyScenario.RepairFailed"/>).</param>
    /// <param name="estimate">The latest repair estimate (for <see cref="RemedyScenario.RepairNotEconomical"/>).</param>
    /// <param name="claimValue">The product's claim value, the base of the economic limit.</param>
    public RemedyEvaluation Evaluate(
        RemedyRules? rules, RemedyScenario scenario, int failedAttempts = 0, decimal? estimate = null, decimal? claimValue = null)
        => throw new NotImplementedException("The remedy evaluator is implemented in T224.");
}
