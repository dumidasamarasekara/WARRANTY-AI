using Warranty.AI.Harness.Context;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;

namespace Warranty.AI.Harness.Agents.Risk;

/// <summary>
/// Computes a run's risk (FR-017, research R23/R25). A capability, not an LLM agent in the PoC; behind
/// an interface so it can become one later (contracts/agents-and-tools.md).
/// </summary>
public interface IRiskAssessor
{
    /// <summary>
    /// Signals available without evidence analysis (<c>PRODUCT_NOT_IN_CATALOG</c>,
    /// <c>DUPLICATE_SERIAL_CLAIM</c>, <c>EVIDENCE_REUSED</c> from upload hashes); stored with
    /// <see cref="RiskAssessmentStage.Intake"/>.
    /// </summary>
    Task<RiskAssessment> AssessAtIntakeAsync(CaseContext @case, IntakeResult intake, CancellationToken ct);

    /// <summary>
    /// All deterministic signals plus the AI-reported ones (evidence and decision outputs); stored with
    /// <see cref="RiskAssessmentStage.Full"/>.
    /// </summary>
    Task<RiskAssessment> AssessFullAsync(AdjudicationContext run, AiRiskReading ai, CancellationToken ct);
}

/// <summary>What the AI reported about risk: the model's own level (display only) and its signals.</summary>
/// <param name="ModelLevel">The decision output's <c>risk.level</c>; stored for display, never used for the computed level.</param>
/// <param name="Signals">AI-sourced signals from the evidence and decision outputs.</param>
public sealed record AiRiskReading(RiskLevel? ModelLevel, IReadOnlyList<RiskSignal> Signals)
{
    public static AiRiskReading None { get; } = new(null, []);
}

/// <summary>Result of the pure scoring rules: merged signals, score and level.</summary>
public sealed record RiskScore(int Score, RiskLevel Level, IReadOnlyList<RiskSignal> Signals);

/// <summary>Deterministic risk assessment (research R23).</summary>
public sealed class RiskAssessor : IRiskAssessor
{
    /// <summary>Score weight of a severity: <c>Low</c> 10, <c>Medium</c> 25, <c>High</c> 40.</summary>
    public static int WeightOf(RiskSeverity severity)
        => throw new NotImplementedException("Pending T064.");

    /// <summary>
    /// Fixed severity of a deterministic signal code (<c>MANIPULATION_ATTEMPT</c>, <c>EVIDENCE_REUSED</c>,
    /// <c>SERIAL_MISMATCH_PHOTO</c> High; <c>DUPLICATE_SERIAL_CLAIM</c>, <c>PRODUCT_NOT_IN_CATALOG</c>,
    /// <c>SOURCE_INCONSISTENCY</c>, <c>PURCHASE_DATE_ANOMALY</c> Medium); AI-sourced signals are Medium.
    /// </summary>
    public static RiskSeverity SeverityOf(RiskSignalCode code, RiskSignalSource source)
        => throw new NotImplementedException("Pending T064.");

    /// <summary><c>Low</c> only without any signal; otherwise <c>High</c> when the score reaches the threshold, else <c>Medium</c>.</summary>
    public static RiskLevel LevelFor(int score, bool anySignal, int riskHighThreshold)
        => throw new NotImplementedException("Pending T064.");

    /// <summary>
    /// Pure scoring: the union of deterministic and AI signals (a code raised by both counted once with
    /// the deterministic severity), score = <c>min(100, Σ weight)</c>, level per <see cref="LevelFor"/>.
    /// The AI's <see cref="AiRiskReading.ModelLevel"/> never changes the result.
    /// </summary>
    public static RiskScore Compute(IReadOnlyList<RiskSignal> deterministicSignals, AiRiskReading ai, int riskHighThreshold)
        => throw new NotImplementedException("Pending T064.");

    public Task<RiskAssessment> AssessAtIntakeAsync(CaseContext @case, IntakeResult intake, CancellationToken ct)
        => throw new NotImplementedException("Pending T064.");

    public Task<RiskAssessment> AssessFullAsync(AdjudicationContext run, AiRiskReading ai, CancellationToken ct)
        => throw new NotImplementedException("Pending T064.");
}
