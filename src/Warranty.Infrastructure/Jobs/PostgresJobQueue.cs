using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Warranty.Application.Abstractions.Jobs;
using Warranty.Domain.Claims;
using Warranty.Infrastructure.Persistence;

namespace Warranty.Infrastructure.Jobs;

/// <summary>
/// The claim job queue on <c>claims.claim_jobs</c> (research R12). Enqueue only tracks the job, so it
/// commits with the caller's claim; dequeue goes through <c>claims.dequeue_claim_job</c> on a
/// <see cref="NoTenantScope"/> connection and returns headers only; complete and fail run in the
/// job's tenant scope, under row-level security.
/// </summary>
internal sealed class PostgresJobQueue(WarrantyDbContext db, TimeProvider time, ILogger<PostgresJobQueue> logger) : IJobQueue
{
    public Task EnqueueAsync(ClaimJob job, CancellationToken ct)
    {
        db.ClaimJobs.Add(job);
        return Task.CompletedTask;
    }

    public async Task<ClaimJobLease?> TryDequeueAsync(string workerId, TimeSpan lockDuration, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        var lockSeconds = (int)Math.Ceiling(lockDuration.TotalSeconds);

        using var _ = NoTenantScope.Begin();
        var leases = await db.Database
            .SqlQuery<ClaimJobLease>($"""
                SELECT job_id AS "JobId", tenant_id AS "TenantId", claim_id AS "ClaimId", round AS "Round", correlation_id AS "CorrelationId"
                FROM claims.dequeue_claim_job({workerId}, {lockSeconds})
                """)
            .ToListAsync(ct);
        return leases.SingleOrDefault();
    }

    public async Task CompleteAsync(Guid jobId, CancellationToken ct)
    {
        var job = await GetAsync(jobId, ct);
        job.Complete();
        await db.SaveChangesAsync(ct);
    }

    public async Task FailAsync(Guid jobId, string reason, CancellationToken ct)
    {
        var job = await GetAsync(jobId, ct);
        job.RecordInfrastructureFailure(time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        logger.LogWarning(
            "Claim job {JobId} failed attempt {Attempts}: {Reason}. Status now {Status}.", jobId, job.Attempts, reason, job.Status);
    }

    private async Task<ClaimJob> GetAsync(Guid jobId, CancellationToken ct)
        => await db.ClaimJobs.SingleOrDefaultAsync(j => j.Id == jobId, ct)
           ?? throw new InvalidOperationException($"Claim job {jobId} is not visible in the current tenant.");
}
