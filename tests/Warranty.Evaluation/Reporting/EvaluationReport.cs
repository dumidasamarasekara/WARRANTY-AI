using Warranty.Evaluation.Metrics;

namespace Warranty.Evaluation.Reporting;

/// <summary>Everything written to <c>report.json</c>; <c>report.md</c> is rendered from the same data.</summary>
public sealed record EvaluationReport
{
    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset FinishedAt { get; init; }

    public required string Mode { get; init; }

    public required bool Record { get; init; }

    public required string EmbeddingProvider { get; init; }

    public required IReadOnlyList<string> Tenants { get; init; }

    public required int CasesSelected { get; init; }

    public required int CasesEvaluated { get; init; }

    /// <summary>Replay cases without recordings: run with every model call failing, scored only by <see cref="Fallback"/>.</summary>
    public required IReadOnlyList<string> CasesNotRecorded { get; init; }

    public required IReadOnlyList<string> CasesFailed { get; init; }

    public required EvaluationMetrics Metrics { get; init; }

    public required IReadOnlyList<TargetCheck> Targets { get; init; }

    public required FallbackCheck Fallback { get; init; }

    public HumanOverrideRateResult? HumanOverrideRate { get; init; }

    public string? HumanOverrideRateError { get; init; }

    /// <summary>What this run can and cannot measure (mode, recordings, embeddings).</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    public required IReadOnlyList<CaseSummary> Cases { get; init; }

    /// <summary>The full observations, including agent outputs, for diagnosis.</summary>
    public required IReadOnlyList<CaseObservation> Observations { get; init; }
}

/// <summary>Expected against actual outcome of one case.</summary>
public sealed record CaseSummary(
    string CaseId,
    string Tenant,
    string Outcome,
    string? Note,
    string? ExpectedRecommendation,
    string? ActualRecommendation,
    bool RecommendationValid,
    int? Confidence,
    string ExpectedDisposition,
    string? ActualDisposition,
    string? Status,
    IReadOnlyList<string> ExpectedEscalationReasons,
    IReadOnlyList<string> ActualEscalationReasons,
    string? RunFailure,
    IReadOnlyList<string> ReasonsFromSkippedSteps);

/// <summary>
/// The AI-unavailable fallback check on replay cases that have no recordings: every model call fails, so
/// FR-031/SC-004 require that none of them is finalized automatically. It measures the deterministic
/// fallback only — never model quality.
/// </summary>
/// <param name="WithoutAiUnavailable">
/// Cases not routed to human review with <c>AI_UNAVAILABLE</c> among the reasons. Listed, not failed: an
/// intake short-circuit (a missing invoice or photo, found without the model) requests that item even
/// when the intake model call failed, which leaves the claim open without finalizing it.
/// </param>
/// <param name="PolicyStepNotRun">
/// Cases whose Policy step did not run (a failed intake skips Evidence, Policy and Decision); their
/// <c>NO_APPLICABLE_POLICY</c> reason, if any, says nothing about the tenant's policies
/// (<see cref="CaseObservation.ReasonsFromSkippedSteps"/>).
/// </param>
public sealed record FallbackCheck(
    int Cases,
    IReadOnlyList<string> AutoFinalized,
    IReadOnlyList<string> WithoutAiUnavailable,
    IReadOnlyList<string> PolicyStepNotRun,
    IReadOnlyDictionary<string, int> Dispositions)
{
    public bool? Passed => Cases == 0 ? null : AutoFinalized.Count == 0;

    public static FallbackCheck Compute(IEnumerable<CaseObservation> notRecorded)
    {
        ArgumentNullException.ThrowIfNull(notRecorded);
        var list = notRecorded.ToList();
        return new FallbackCheck(
            list.Count,
            list.Where(o => o.Disposition is "AutoApprove" or "AutoReject").Select(o => o.CaseId).ToList(),
            list.Where(o => o.Disposition != "HumanReview" || !o.EscalationReasons.Contains("AI_UNAVAILABLE", StringComparer.Ordinal))
                .Select(o => o.CaseId).ToList(),
            list.Where(o => !o.PolicyStepRan).Select(o => o.CaseId).ToList(),
            list.GroupBy(o => o.Disposition ?? "none", StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));
    }
}
