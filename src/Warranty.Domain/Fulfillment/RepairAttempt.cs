namespace Warranty.Domain.Fulfillment;

/// <summary>
/// One attempt to repair the product (<c>claims.repair_attempts</c>), numbered per repair from 1.
/// <see cref="AttemptResult.Open"/> until the attempt ends. Created and changed only through
/// <see cref="Repair"/>; a transfer moves the open attempt to the new centre.
/// </summary>
public sealed class RepairAttempt
{
    private RepairAttempt()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid RepairId { get; private set; }

    /// <summary>Per repair, from 1.</summary>
    public int Seq { get; private set; }

    public Guid ServiceCenterId { get; private set; }

    public AttemptResult Result { get; private set; }

    /// <summary>Required for <see cref="AttemptResult.Failed"/> and <see cref="AttemptResult.NotEconomical"/>; staff-only.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Latest estimate reported by the centre.</summary>
    public decimal? EstimateAmount { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public bool IsOpen => Result == AttemptResult.Open;

    internal static RepairAttempt Start(Guid tenantId, Guid repairId, int seq, Guid serviceCenterId, DateTimeOffset startedAt)
    {
        if (tenantId == Guid.Empty || repairId == Guid.Empty || serviceCenterId == Guid.Empty)
        {
            throw new ArgumentException("Tenant, repair and service-centre IDs are required.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seq);

        return new RepairAttempt
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            RepairId = repairId,
            Seq = seq,
            ServiceCenterId = serviceCenterId,
            Result = AttemptResult.Open,
            StartedAt = startedAt,
        };
    }

    /// <summary>The repair centre has finished: the open attempt ends as <see cref="AttemptResult.Ready"/>.</summary>
    internal void MarkReady(DateTimeOffset endedAt)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException($"Repair attempt {Seq} is not open ({Result}).");
        }

        Result = AttemptResult.Ready;
        EndedAt = endedAt;
    }

    /// <summary>Keeps the latest estimate the centre reported for this open attempt.</summary>
    internal void ReportEstimate(decimal estimateAmount)
    {
        RequireOpen();
        ArgumentOutOfRangeException.ThrowIfNegative(estimateAmount);
        EstimateAmount = estimateAmount;
    }

    /// <summary>The repair did not fix the product: the open attempt ends as <see cref="AttemptResult.Failed"/> with its reason.</summary>
    internal void EndFailed(string reason, DateTimeOffset endedAt) => End(AttemptResult.Failed, reason, endedAt);

    /// <summary>The estimate is above the economic limit: the open attempt ends as <see cref="AttemptResult.NotEconomical"/>.</summary>
    internal void EndNotEconomical(string reason, DateTimeOffset endedAt) => End(AttemptResult.NotEconomical, reason, endedAt);

    /// <summary>The customer collected the product: a <see cref="AttemptResult.Ready"/> attempt becomes <see cref="AttemptResult.Collected"/>.</summary>
    internal void MarkCollected()
    {
        if (Result != AttemptResult.Ready)
        {
            throw new InvalidOperationException($"Repair attempt {Seq} is not ready for collection ({Result}).");
        }

        Result = AttemptResult.Collected;
    }

    private void End(AttemptResult result, string reason, DateTimeOffset endedAt)
    {
        RequireOpen();
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Result = result;
        FailureReason = reason.Trim();
        EndedAt = endedAt;
    }

    private void RequireOpen()
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException($"Repair attempt {Seq} is not open ({Result}).");
        }
    }
}
