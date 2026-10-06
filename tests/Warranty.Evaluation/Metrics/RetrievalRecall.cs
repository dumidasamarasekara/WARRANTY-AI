namespace Warranty.Evaluation.Metrics;

public sealed record CaseRecall(string CaseId, IReadOnlyList<string> ExpectedKeys, IReadOnlyList<string> RankedKeys, IReadOnlyDictionary<int, double> RecallAtK);

public sealed record RetrievalRecallResult(
    IReadOnlyList<int> Ks,
    IReadOnlyDictionary<int, double?> MeanRecallAtK,
    int CasesScored,
    IReadOnlyList<string> CasesWithoutRetrieval,
    IReadOnlyList<CaseRecall> Cases);

/// <summary>
/// Retrieval recall@k of the expected clause keys (research R19): the share of a case's
/// <c>expected.citedClauseKeys</c> found among the first k distinct clause keys the run retrieved,
/// ranked by similarity score, averaged over cases (macro). Only cases that expect a recommendation and
/// list clause keys are scored — intake short-circuit cases never reach retrieval by design. A scored case
/// whose run retrieved nothing scores 0 and is listed.
/// </summary>
public static class RetrievalRecall
{
    public static readonly IReadOnlyList<int> DefaultKs = [1, 3, 5, 10];

    public static RetrievalRecallResult Compute(IEnumerable<ScoredCase> cases, IReadOnlyList<int>? ks = null)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ks ??= DefaultKs;
        var scored = cases
            .Where(c => c.Expected.Recommendation is not null && c.Expected.CitedClauseKeys.Count > 0)
            .Select(c =>
            {
                var ranked = Rank(c.Observation.RetrievedClauses);
                return new CaseRecall(
                    c.CaseId,
                    c.Expected.CitedClauseKeys,
                    ranked,
                    ks.ToDictionary(k => k, k => RecallAt(c.Expected.CitedClauseKeys, ranked, k)));
            })
            .ToList();

        return new RetrievalRecallResult(
            ks,
            ks.ToDictionary(k => k, k => scored.Count == 0 ? (double?)null : scored.Average(c => c.RecallAtK[k])),
            scored.Count,
            scored.Where(c => c.RankedKeys.Count == 0).Select(c => c.CaseId).ToList(),
            scored);
    }

    /// <summary>Distinct clause keys, best score first (ties by issue order of the reference).</summary>
    public static IReadOnlyList<string> Rank(IEnumerable<RetrievedClause> retrieved)
    {
        ArgumentNullException.ThrowIfNull(retrieved);
        return retrieved
            .OrderByDescending(r => r.Score)
            .ThenBy(r => RefNumber(r.RefId))
            .Select(r => r.ClauseKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>|expected ∩ top-k| / |expected|; 1 when nothing is expected.</summary>
    public static double RecallAt(IReadOnlyCollection<string> expected, IReadOnlyList<string> rankedKeys, int k)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(rankedKeys);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);
        var wanted = expected.ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return 1d;
        }

        return (double)rankedKeys.Take(k).Count(wanted.Contains) / wanted.Count;
    }

    private static int RefNumber(string refId)
        => int.TryParse(refId.AsSpan(refId.LastIndexOf('-') + 1), out var n) ? n : int.MaxValue;
}
