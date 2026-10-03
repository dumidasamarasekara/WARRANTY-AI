using Warranty.Domain.Claims;

namespace Warranty.Domain.Adjudication;

/// <summary>One harness run for one claim submission round (unique per claim and round).</summary>
public sealed class AdjudicationRun
{
    private IReadOnlyDictionary<string, Guid> _referenceMap = new Dictionary<string, Guid>();

    private AdjudicationRun()
    {
        CorrelationId = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ClaimId { get; private set; }

    public int Round { get; private set; }

    public string CorrelationId { get; private set; }

    public RunStatus Status { get; private set; }

    public RunStep CurrentStep { get; private set; }

    /// <summary>Null until the guardrails ran.</summary>
    public Disposition? Disposition { get; private set; }

    /// <summary>Why AI analysis could not be completed (FR-031).</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Harness-issued reference IDs (<c>EV-n</c>, <c>POL-n</c>, <c>GLB-n</c>) and the rows they point to.</summary>
    public IReadOnlyDictionary<string, Guid> ReferenceMap => _referenceMap;

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static AdjudicationRun Start(Guid id, Guid tenantId, Guid claimId, int round, string correlationId, DateTimeOffset now)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || claimId == Guid.Empty)
        {
            throw new ArgumentException("Run, tenant and claim IDs are required.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(round);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        return new AdjudicationRun
        {
            Id = id,
            TenantId = tenantId,
            ClaimId = claimId,
            Round = round,
            CorrelationId = correlationId,
            Status = RunStatus.Running,
            CurrentStep = RunStep.Intake,
            StartedAt = now,
        };
    }

    /// <summary>Records the checkpoint; a step can never move backwards.</summary>
    public void AdvanceTo(RunStep step)
    {
        EnsureRunning();
        if (step < CurrentStep)
        {
            throw new InvalidOperationException($"Run {Id} cannot move back from {CurrentStep} to {step}.");
        }

        CurrentStep = step;
    }

    public void RecordReferences(IReadOnlyDictionary<string, Guid> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        EnsureRunning();
        _referenceMap = new Dictionary<string, Guid>(references, StringComparer.Ordinal);
    }

    public void Complete(Disposition disposition, DateTimeOffset now)
    {
        EnsureRunning();
        Disposition = disposition;
        CurrentStep = RunStep.Done;
        Status = RunStatus.Completed;
        CompletedAt = now;
    }

    /// <summary>
    /// Records that AI analysis could not be completed. The run still reaches the guardrails, which
    /// route the claim to human review (FR-031), so only the reason is stored here.
    /// </summary>
    public void RecordAiFailure(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureRunning();
        FailureReason = reason;
    }

    /// <summary>An unrecoverable infrastructure failure; the job may retry the round.</summary>
    public void Fail(string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Status = RunStatus.Failed;
        FailureReason = reason;
        CompletedAt = now;
    }

    private void EnsureRunning()
    {
        if (Status != RunStatus.Running)
        {
            throw new InvalidOperationException($"Run {Id} is {Status}.");
        }
    }
}
