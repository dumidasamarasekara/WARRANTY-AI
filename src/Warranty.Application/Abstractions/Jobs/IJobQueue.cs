using Warranty.Domain.Claims;

namespace Warranty.Application.Abstractions.Jobs;

/// <summary>
/// Postgres-backed claim job queue (research R12). Enqueue joins the caller's transaction; dequeue
/// returns job headers only, and the worker opens a tenant scope from the returned tenant ID.
/// </summary>
public interface IJobQueue
{
    /// <summary>Adds a job inside the current unit of work (same transaction as the claim).</summary>
    Task EnqueueAsync(ClaimJob job, CancellationToken ct);

    /// <summary>Claims the next available job across tenants, or returns null when there is none.</summary>
    Task<ClaimJobLease?> TryDequeueAsync(string workerId, TimeSpan lockDuration, CancellationToken ct);

    /// <summary>Marks the job done; runs inside the job's tenant scope.</summary>
    Task CompleteAsync(Guid jobId, CancellationToken ct);

    /// <summary>Re-queues with backoff or fails permanently after the maximum attempts; runs inside the job's tenant scope.</summary>
    Task FailAsync(Guid jobId, string reason, CancellationToken ct);
}

/// <summary>The five job header columns returned by <c>claims.dequeue_claim_job</c>.</summary>
public sealed record ClaimJobLease(Guid JobId, Guid TenantId, Guid ClaimId, int Round, string CorrelationId);
