using System.Text.Json.Nodes;

namespace Warranty.Evaluation.Metrics;

/// <summary>Whether a golden case produced an observation the metrics can score.</summary>
public enum CaseOutcomeKind
{
    /// <summary>The case ran end to end; every metric scores it.</summary>
    Evaluated,

    /// <summary>Replay mode only: the case has no recorded model responses, so it was not run and is excluded from every metric.</summary>
    NotRecorded,

    /// <summary>The run threw an infrastructure error; excluded from the metrics and listed in the report.</summary>
    Failed,
}

/// <summary>A policy clause retrieved for the run (<c>adjudication.retrieved_policy_refs</c>).</summary>
public sealed record RetrievedClause(string RefId, string ClauseKey, double Score);

/// <summary>Token and cost totals of a run's model calls (<c>aiops.model_calls</c>).</summary>
public sealed record UsageTotals(
    int ModelCalls,
    int FailedCalls,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    decimal Cost)
{
    public static readonly UsageTotals Zero = new(0, 0, 0, 0, 0, 0, 0m);

    public UsageTotals Add(UsageTotals other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new UsageTotals(
            ModelCalls + other.ModelCalls,
            FailedCalls + other.FailedCalls,
            InputTokens + other.InputTokens,
            OutputTokens + other.OutputTokens,
            CacheReadTokens + other.CacheReadTokens,
            CacheWriteTokens + other.CacheWriteTokens,
            Cost + other.Cost);
    }
}

/// <summary>
/// What the system produced for one golden case, read back from the evaluation database after the run.
/// Enum values use the golden dataset's spelling: dispositions, statuses and risk levels as their C#
/// names (<c>AutoApprove</c>), recommendation, coverage, reason and signal codes as wire names (<c>APPROVE</c>).
/// </summary>
public sealed record CaseObservation
{
    public required string CaseId { get; init; }

    public required string Tenant { get; init; }

    public CaseOutcomeKind Outcome { get; init; } = CaseOutcomeKind.Evaluated;

    /// <summary>Why a case was not evaluated (missing recordings, an exception).</summary>
    public string? Note { get; init; }

    public DateOnly ClaimDate { get; init; }

    /// <summary>The recommendation's decision; null when there is none or it was too malformed to read.</summary>
    public string? Recommendation { get; init; }

    /// <summary>True when the recommendation passed schema and reference validation.</summary>
    public bool RecommendationValid { get; init; }

    /// <summary>The model-reported confidence 0–100 of the recommendation.</summary>
    public int? Confidence { get; init; }

    public string? Coverage { get; init; }

    public string? Disposition { get; init; }

    public string? Status { get; init; }

    public IReadOnlyList<string> EscalationReasons { get; init; } = [];

    public string? RiskLevel { get; init; }

    public IReadOnlyList<string> RiskSignals { get; init; } = [];

    public IReadOnlyList<string> RequestedItems { get; init; } = [];

    /// <summary>The run's failure reason (an AI step that failed, refused or timed out), if any.</summary>
    public string? RunFailure { get; init; }

    /// <summary>Intake Agent output (intake-extraction schema).</summary>
    public JsonObject? IntakeExtraction { get; init; }

    /// <summary>Evidence Agent invoice extraction, if an invoice was analysed.</summary>
    public JsonObject? InvoiceExtraction { get; init; }

    /// <summary>Photo analyses in upload order (null where a photo has no finding).</summary>
    public IReadOnlyList<JsonObject?> PhotoAnalyses { get; init; } = [];

    public IReadOnlyList<RetrievedClause> RetrievedClauses { get; init; } = [];

    /// <summary>Every reference the harness issued for the run (<c>EV-n</c>, <c>POL-n</c>, <c>GLB-n</c>).</summary>
    public IReadOnlyList<string> IssuedReferences { get; init; } = [];

    /// <summary>
    /// Every reference the recommendation's raw output uses: the <c>evidenceRefs</c> and <c>policyRefs</c>
    /// entries and the IDs mentioned in its reasoning summary, as written by the model.
    /// </summary>
    public IReadOnlyList<string> CitedReferences { get; init; } = [];

    public UsageTotals Usage { get; init; } = UsageTotals.Zero;

    public IReadOnlyDictionary<string, UsageTotals> UsageByAgent { get; init; } = new Dictionary<string, UsageTotals>();
}
