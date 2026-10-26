using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;

namespace Warranty.Guardrails.Remedies;

/// <summary>
/// The one deterministic remedy evaluator (002 research R2): a pure function of the policy version's remedy
/// rules, the scenario and the repair facts. Every company runs the same code; only the rules differ (FR-008).
/// </summary>
/// <remarks>
/// Rules: a missing rule set, row or fact needed for the decision goes to a reviewer with
/// <see cref="RemedyPendingReason.NoRemedyRule"/>, never to an automatic remedy (FR-012). A single remedy is
/// set automatically (FR-011) except after the attempt limit or an uneconomical estimate, where a reviewer
/// confirms it (FR-031, FR-032), and when a replacement is not available (003 FR-026). Without an economic limit the repair is never uneconomical (FR-004); an
/// estimate equal to the limit is economical.
/// </remarks>
public sealed class RemedyPolicyEvaluator
{
    /// <param name="rules">The applicable policy version's remedy rules; null when the version has none.</param>
    /// <param name="scenario">The scenario to evaluate (research R13).</param>
    /// <param name="failedAttempts">Failed repair attempts so far (for <see cref="RemedyScenario.RepairFailed"/>).</param>
    /// <param name="estimate">The latest repair estimate (for <see cref="RemedyScenario.RepairNotEconomical"/>).</param>
    /// <param name="claimValue">The product's claim value, the base of the economic limit.</param>
    public RemedyEvaluation Evaluate(
        RemedyRules? rules, RemedyScenario scenario, int failedAttempts = 0, decimal? estimate = null, decimal? claimValue = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(failedAttempts);
        var row = TableRowOf(scenario);
        if (rules is null)
        {
            return Review(scenario, row, [], RemedyPendingReason.NoRemedyRule);
        }

        return scenario switch
        {
            RemedyScenario.CoveredDefect => FromRow(scenario, row, rules.CoveredDefect),
            RemedyScenario.ProtectionPlanDamage => rules.ProtectionPlan is { LiftsAccidentalDamageExclusion: true } plan
                ? FromRow(scenario, row, plan)
                : Review(scenario, row, [], RemedyPendingReason.NoRemedyRule),
            RemedyScenario.RepairFailed => RepairFailed(scenario, row, rules.RepairFailed, failedAttempts),
            RemedyScenario.RepairNotEconomical => RepairNotEconomical(scenario, row, rules.RepairNotEconomical, estimate, claimValue),
            RemedyScenario.ReplacementNotAvailable => ReplacementNotAvailable(scenario, row, rules.ReplacementNotAvailable),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown remedy scenario."),
        };
    }

    /// <summary>The remedy-table row a scenario reads, named as in the <c>remedies</c> block.</summary>
    public static string TableRowOf(RemedyScenario scenario) => scenario switch
    {
        RemedyScenario.CoveredDefect => "coveredDefect",
        RemedyScenario.ProtectionPlanDamage => "protectionPlan",
        RemedyScenario.RepairNotEconomical => "repairNotEconomical",
        RemedyScenario.RepairFailed => "repairFailed",
        RemedyScenario.ReplacementNotAvailable => "replacementNotAvailable",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown remedy scenario."),
    };

    /// <summary>An initial-scenario row: one remedy is set automatically, a list is a reviewer's choice.</summary>
    private static RemedyEvaluation FromRow(RemedyScenario scenario, string row, RemedyChoice? choice)
    {
        if (choice is null || choice.AllowedRemedies.Count == 0)
        {
            return Review(scenario, row, [], RemedyPendingReason.NoRemedyRule);
        }

        return choice.NeedsReviewer
            ? Review(scenario, row, choice.AllowedRemedies, RemedyPendingReason.PolicyChoice)
            : new RemedyEvaluation(scenario, row, [.. choice.AllowedRemedies], choice.AllowedRemedies[0], false, null, false, null);
    }

    private static RemedyEvaluation RepairFailed(RemedyScenario scenario, string row, RepairFailedRule? rule, int failedAttempts)
    {
        if (rule is null)
        {
            return Review(scenario, row, [], RemedyPendingReason.NoRemedyRule);
        }

        return failedAttempts < rule.AfterFailedAttempts
            ? Continue(scenario, row, limit: null)
            : Review(scenario, row, rule.AllowedRemedies, RemedyPendingReason.RepairFailedLimit);
    }

    private static RemedyEvaluation RepairNotEconomical(
        RemedyScenario scenario, string row, RepairNotEconomicalRule? rule, decimal? estimate, decimal? claimValue)
    {
        if (rule is null)
        {
            return Continue(scenario, row, limit: null);
        }

        decimal? limit = claimValue is { } value ? value * rule.LimitPercentOfClaimValue / 100m : null;
        if (estimate is null || limit is null)
        {
            return Review(scenario, row, rule.AllowedRemedies, RemedyPendingReason.NoRemedyRule, limit);
        }

        return estimate <= limit
            ? Continue(scenario, row, limit)
            : Review(scenario, row, rule.AllowedRemedies, RemedyPendingReason.RepairNotEconomical, limit);
    }

    /// <summary>
    /// A replacement that cannot be supplied (003 FR-026, research R2): the row's remedies go to a reviewer with
    /// <see cref="RemedyPendingReason.ReplacementNotAvailable"/>; a single remedy is the recommendation but is never set
    /// automatically. A missing row gives no remedy (<see cref="RemedyPendingReason.NoRemedyRule"/>).
    /// </summary>
    private static RemedyEvaluation ReplacementNotAvailable(RemedyScenario scenario, string row, RemedyChoice? choice)
        => choice is null || choice.AllowedRemedies.Count == 0
            ? Review(scenario, row, [], RemedyPendingReason.NoRemedyRule)
            : Review(scenario, row, choice.AllowedRemedies, RemedyPendingReason.ReplacementNotAvailable);

    private static RemedyEvaluation Review(
        RemedyScenario scenario, string row, IReadOnlyList<RemedyType> allowed, RemedyPendingReason reason, decimal? limit = null)
        => new(scenario, row, [.. allowed], allowed.Count == 1 ? allowed[0] : null, true, reason, false, limit);

    private static RemedyEvaluation Continue(RemedyScenario scenario, string row, decimal? limit)
        => new(scenario, row, [], null, false, null, true, limit);
}
