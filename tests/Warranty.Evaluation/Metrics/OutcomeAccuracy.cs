namespace Warranty.Evaluation.Metrics;

public sealed record AccuracyResult(
    Ratio Overall,
    IReadOnlyDictionary<string, Ratio> ByTenant,
    IReadOnlyDictionary<string, Ratio> ByExpected,
    IReadOnlyList<string> MissedCaseIds);

/// <summary>
/// Recommendation and disposition accuracy (SC-005 target ≥ 85% per tenant). A recommendation counts only
/// when it is valid and its decision equals the label; an invalid or missing one is a miss. Cases labelled
/// with no recommendation (intake short-circuit) are scored on disposition only.
/// </summary>
public static class OutcomeAccuracy
{
    public static AccuracyResult Recommendation(IEnumerable<ScoredCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        return Compute(
            cases.Where(c => c.Expected.Recommendation is not null),
            c => c.Expected.Recommendation!,
            c => c.Observation.RecommendationValid
                 && string.Equals(c.Observation.Recommendation, c.Expected.Recommendation, StringComparison.Ordinal));
    }

    public static AccuracyResult Disposition(IEnumerable<ScoredCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        return Compute(
            cases,
            c => c.Expected.Disposition,
            c => string.Equals(c.Observation.Disposition, c.Expected.Disposition, StringComparison.Ordinal));
    }

    private static AccuracyResult Compute(IEnumerable<ScoredCase> cases, Func<ScoredCase, string> label, Func<ScoredCase, bool> hit)
    {
        var list = cases.ToList();
        return new AccuracyResult(
            list.Aggregate(Ratio.Empty, (ratio, c) => ratio.Add(hit(c))),
            Ratio.By(list, c => c.Tenant, hit),
            Ratio.By(list, label, hit),
            list.Where(c => !hit(c)).Select(c => c.CaseId).ToList());
    }
}
