using Microsoft.EntityFrameworkCore;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Fulfillment;

namespace Warranty.Infrastructure.Persistence.Repositories;

/// <summary>
/// Remedies of the current tenant's claims. Remedy decisions are insert-only: they are read untracked and
/// this repository has no update or delete for them (data-model.md).
/// </summary>
internal sealed class RemedyRepository(WarrantyDbContext db) : IRemedyRepository
{
    public Task<ClaimRemedy?> GetAsync(Guid claimId, CancellationToken ct)
        => db.ClaimRemedies.SingleOrDefaultAsync(r => r.ClaimId == claimId, ct);

    public void Add(ClaimRemedy remedy) => db.ClaimRemedies.Add(remedy);

    public void AddDecision(RemedyDecision decision) => db.RemedyDecisions.Add(decision);

    public async Task<IReadOnlyList<RemedyDecision>> GetDecisionsAsync(Guid claimId, CancellationToken ct)
        => await db.RemedyDecisions.AsNoTracking().Where(d => d.ClaimId == claimId).OrderBy(d => d.Seq).ToListAsync(ct);

    public Task<RemedyCompletion?> GetCompletionAsync(Guid claimId, CancellationToken ct)
        => db.RemedyCompletions.AsNoTracking().SingleOrDefaultAsync(c => c.ClaimId == claimId, ct);

    public void AddCompletion(RemedyCompletion completion) => db.RemedyCompletions.Add(completion);
}

/// <summary>
/// Repairs of the current tenant's claims, loaded with their hand-overs, attempts and events. Repair events
/// live in the append-only <c>audit.repair_events</c>: they are only ever added, never updated or deleted.
/// </summary>
internal sealed class RepairRepository(WarrantyDbContext db) : IRepairRepository
{
    public Task<Repair?> GetByClaimAsync(Guid claimId, CancellationToken ct)
        => db.Repairs
            .Include(r => r.Handovers)
            .Include(r => r.Attempts.OrderBy(a => a.Seq))
            .Include(r => r.Events.OrderBy(e => e.Seq))
            .AsSplitQuery()
            .SingleOrDefaultAsync(r => r.ClaimId == claimId, ct);

    public void Add(Repair repair) => db.Repairs.Add(repair);

    public void AddEvent(RepairEvent repairEvent)
    {
        ArgumentNullException.ThrowIfNull(repairEvent);
        var entry = db.Entry(repairEvent);
        if (entry.State is EntityState.Detached)
        {
            db.RepairEvents.Add(repairEvent);
        }
        else if (entry.State is not EntityState.Added)
        {
            throw new InvalidOperationException("Repair events are append-only; a saved event cannot be added again.");
        }
    }
}
