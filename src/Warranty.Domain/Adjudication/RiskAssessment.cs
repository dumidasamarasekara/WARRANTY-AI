using Warranty.Domain.Claims;

namespace Warranty.Domain.Adjudication;

/// <summary>One risk signal found for a claim (FR-017).</summary>
public sealed record RiskSignal(
    RiskSignalCode Code,
    RiskSignalSource Source,
    RiskSeverity Severity,
    string Detail,
    IReadOnlyList<string> EvidenceRefs);

/// <summary>Computed risk of a run. Level and score are derived by IRiskAssessor (research R23).</summary>
public sealed class RiskAssessment
{
    private RiskAssessment()
    {
        Signals = [];
    }

    public Guid RunId { get; private set; }

    public Guid TenantId { get; private set; }

    public RiskAssessmentStage Stage { get; private set; }

    public int Score { get; private set; }

    public RiskLevel Level { get; private set; }

    public IReadOnlyList<RiskSignal> Signals { get; private set; }

    public static RiskAssessment Create(
        Guid runId, Guid tenantId, RiskAssessmentStage stage, int score, RiskLevel level, IEnumerable<RiskSignal> signals)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(score);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(score, 100);
        var list = signals.ToArray();

        // Low means "no signal of any kind" (R23); a stored assessment can never contradict that.
        if ((level == RiskLevel.Low) != (list.Length == 0))
        {
            throw new ArgumentException("Risk level is Low exactly when no risk signal is present.", nameof(level));
        }

        return new RiskAssessment { RunId = runId, TenantId = tenantId, Stage = stage, Score = score, Level = level, Signals = list };
    }
}
