using Microsoft.EntityFrameworkCore;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Domain.Audit;
using Warranty.Domain.Common;
using Warranty.Infrastructure.Persistence;

namespace Warranty.Infrastructure.Audit;

/// <summary>
/// Appends hash-chained trail entries for the current tenant's claims (FR-038, FR-039). Each append
/// takes a per-claim transaction-scoped advisory lock, reads the last <c>seq</c> and hash, and saves
/// in the caller's transaction (or its own), so the sequence stays gap-free and concurrent appends
/// cannot fork the chain. Saving also flushes the unit of work's pending changes, so a step and its
/// trail entry commit together.
/// </summary>
internal sealed class DecisionTrailWriter(WarrantyDbContext db, ITenantContext tenantContext, TimeProvider time) : IDecisionTrailWriter
{
    public async Task AppendAsync(Guid claimId, TrailStep step, string actor, string summary, object? payload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        var tenantId = tenantContext.TenantId;
        var canonicalPayload = CanonicalJson.Serialize(payload);

        if (db.Database.CurrentTransaction is not null)
        {
            await AppendLockedAsync(tenantId, claimId, step, actor, summary, canonicalPayload, ct);
            return;
        }

        await db.Database.CreateExecutionStrategy().ExecuteAsync(
            async token =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                await AppendLockedAsync(tenantId, claimId, step, actor, summary, canonicalPayload, token);
                await transaction.CommitAsync(token);
            },
            ct);
    }

    private async Task AppendLockedAsync(
        Guid tenantId, Guid claimId, TrailStep step, string actor, string summary, string canonicalPayload, CancellationToken ct)
    {
        var lockKey = BitConverter.ToInt64(claimId.ToByteArray(), 0);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({lockKey})", ct);

        var last = await db.DecisionTrailEntries.AsNoTracking()
            .Where(e => e.ClaimId == claimId)
            .OrderByDescending(e => e.Seq)
            .Select(e => new { e.Seq, e.Hash })
            .FirstOrDefaultAsync(ct);
        var seq = (last?.Seq ?? 0) + 1;
        var prevHash = last?.Hash ?? TrailHash.Genesis;
        var occurredAt = TrailHash.Truncate(time.GetUtcNow());
        var correlationId = tenantContext.CorrelationId;
        var hash = TrailHash.Compute(prevHash, tenantId, claimId, seq, occurredAt, step, actor, summary, canonicalPayload, correlationId);

        var entry = DecisionTrailEntry.Create(
            tenantId, claimId, seq, occurredAt, step, actor, summary, canonicalPayload, correlationId, prevHash, hash);
        db.DecisionTrailEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        // The next append reads its seq from the database, so an entry left pending here would be inserted
        // later with a seq that is already taken — and a step could commit without it. Fail the step's
        // transaction instead; the job then retries the step from its checkpoint.
        if (db.Entry(entry).State != EntityState.Unchanged)
        {
            throw new InvalidOperationException(
                $"Decision trail entry {seq} of claim {claimId} was not saved; the unit of work's change tracking is inconsistent.");
        }
    }
}
