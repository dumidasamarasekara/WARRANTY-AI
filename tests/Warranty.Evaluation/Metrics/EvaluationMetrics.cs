namespace Warranty.Evaluation.Metrics;

/// <summary>Every golden-set metric over the evaluated cases (research R19, quickstart §6).</summary>
public sealed record EvaluationMetrics(
    int CasesEvaluated,
    ExtractionAccuracyResult Extraction,
    RetrievalRecallResult Retrieval,
    AccuracyResult Recommendation,
    AccuracyResult Disposition,
    EscalationRecallResult Escalation,
    CalibrationResult Calibration,
    UnsupportedReferenceResult UnsupportedReferences,
    UsageSummary Usage)
{
    /// <summary>SC-005: recommendation (and disposition) accuracy at least 85%, per tenant.</summary>
    public const double AccuracyTarget = 0.85;

    /// <summary>Scores the cases whose observation is <see cref="CaseOutcomeKind.Evaluated"/>; the others are excluded.</summary>
    public static EvaluationMetrics Compute(IEnumerable<ScoredCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var evaluated = cases.Where(c => c.Observation.Outcome == CaseOutcomeKind.Evaluated).ToList();
        return new EvaluationMetrics(
            evaluated.Count,
            ExtractionAccuracy.Compute(evaluated),
            RetrievalRecall.Compute(evaluated),
            OutcomeAccuracy.Recommendation(evaluated),
            OutcomeAccuracy.Disposition(evaluated),
            EscalationRecall.Compute(evaluated),
            Metrics.Calibration.Compute(evaluated),
            Metrics.UnsupportedReferences.Compute(evaluated),
            UsageSummary.Compute(evaluated));
    }

    /// <summary>The release targets of quickstart §6 with their verdict; <c>null</c> = not measured (nothing scored).</summary>
    public IReadOnlyList<TargetCheck> Targets()
    {
        var checks = new List<TargetCheck>();
        foreach (var (tenant, ratio) in Recommendation.ByTenant)
        {
            checks.Add(new TargetCheck($"Recommendation accuracy ({tenant})", "≥ 85% (SC-005)", ratio.ToString(), ratio.Value is { } v ? v >= AccuracyTarget : null));
        }

        foreach (var (tenant, ratio) in Disposition.ByTenant)
        {
            checks.Add(new TargetCheck($"Disposition accuracy ({tenant})", "≥ 85%", ratio.ToString(), ratio.Value is { } v ? v >= AccuracyTarget : null));
        }

        checks.Add(new TargetCheck(
            "Escalation recall", "100% (SC-004)", Escalation.Recall.ToString(), Escalation.Recall.Value is { } recall ? recall >= 1d : null));
        checks.Add(new TargetCheck(
            "Unsupported-reference rate",
            "0 (SC-006)",
            UnsupportedReferences.Rate is { } rate ? $"{rate:P1} ({UnsupportedReferences.Unsupported}/{UnsupportedReferences.CitedReferences})" : "n/a (no citations)",
            UnsupportedReferences.Rate is { } r ? r == 0d : null));
        return checks;
    }
}

/// <summary>One target line of the report; <see cref="Passed"/> is null when the metric had nothing to score.</summary>
public sealed record TargetCheck(string Metric, string Target, string Actual, bool? Passed);
