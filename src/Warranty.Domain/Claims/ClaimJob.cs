namespace Warranty.Domain.Claims;

public enum ClaimJobStatus
{
    Queued,
    Running,
    Done,
    Failed,
}

/// <summary>
/// Queued adjudication work for one claim round. The job's tenant is the only tenant source for the
/// worker (research R9); attempts count infrastructure failures only — AI failures route the claim
/// to human review instead of retrying (FR-031).
/// </summary>
public sealed class ClaimJob
{
    public const int MaxAttempts = 3;

    private ClaimJob()
    {
        CorrelationId = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ClaimId { get; private set; }

    public int Round { get; private set; }

    public ClaimJobStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset AvailableAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public string CorrelationId { get; private set; }

    public static ClaimJob Enqueue(Guid id, Guid tenantId, Guid claimId, int round, string correlationId, DateTimeOffset now)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || claimId == Guid.Empty)
        {
            throw new ArgumentException("Job, tenant and claim IDs are required.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(round);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        return new ClaimJob
        {
            Id = id,
            TenantId = tenantId,
            ClaimId = claimId,
            Round = round,
            Status = ClaimJobStatus.Queued,
            AvailableAt = now,
            CorrelationId = correlationId,
        };
    }

    public void Complete()
    {
        Status = ClaimJobStatus.Done;
        LockedUntil = null;
    }

    /// <summary>Re-queues with exponential backoff, or fails permanently after <see cref="MaxAttempts"/>.</summary>
    public void RecordInfrastructureFailure(DateTimeOffset now)
    {
        LockedUntil = null;
        if (Attempts >= MaxAttempts)
        {
            Status = ClaimJobStatus.Failed;
            return;
        }

        Status = ClaimJobStatus.Queued;
        AvailableAt = now + BackoffFor(Attempts);
    }

    public static TimeSpan BackoffFor(int attempts) => TimeSpan.FromSeconds(5 * Math.Pow(2, Math.Max(0, attempts - 1)));
}
