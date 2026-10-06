namespace Warranty.Evaluation.Metrics;

/// <summary>
/// Model usage of the evaluated cases (<c>aiops.model_calls</c>): calls, input, output, cache-read and
/// cache-write tokens and the estimated cost the gateway priced them at. Replayed calls carry the tokens
/// recorded with the fixture; their cost is whatever the gateway's price table gives those tokens.
/// </summary>
public sealed record UsageSummary(UsageTotals Total, IReadOnlyDictionary<string, UsageTotals> ByAgent, int Cases)
{
    public decimal? CostPerCase => Cases == 0 ? null : Total.Cost / Cases;

    /// <summary>Share of input-side tokens served from the prompt cache.</summary>
    public double? CacheReadShare
        => Total.InputTokens + Total.CacheReadTokens + Total.CacheWriteTokens == 0
            ? null
            : (double)Total.CacheReadTokens / (Total.InputTokens + Total.CacheReadTokens + Total.CacheWriteTokens);

    public static UsageSummary Compute(IEnumerable<ScoredCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var observations = cases.Select(c => c.Observation).ToList();
        var byAgent = new SortedDictionary<string, UsageTotals>(StringComparer.Ordinal);
        foreach (var (agent, usage) in observations.SelectMany(o => o.UsageByAgent))
        {
            byAgent[agent] = (byAgent.TryGetValue(agent, out var sum) ? sum : UsageTotals.Zero).Add(usage);
        }

        return new UsageSummary(
            observations.Aggregate(UsageTotals.Zero, (sum, o) => sum.Add(o.Usage)),
            byAgent,
            observations.Count);
    }
}
