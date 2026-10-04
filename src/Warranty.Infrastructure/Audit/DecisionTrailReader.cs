using Microsoft.EntityFrameworkCore;
using Warranty.Application.Abstractions.Audit;
using Warranty.Infrastructure.Persistence;

namespace Warranty.Infrastructure.Audit;

/// <summary>
/// Loads a claim's trail entries of the current tenant (query filter and RLS) in <c>seq</c> order and
/// re-verifies the hash chain over exactly the entries returned.
/// </summary>
internal sealed class DecisionTrailReader(WarrantyDbContext db) : IDecisionTrailReader
{
    public async Task<DecisionTrailSnapshot> ReadAsync(Guid claimId, CancellationToken ct)
    {
        var entries = await db.DecisionTrailEntries.AsNoTracking()
            .Where(e => e.ClaimId == claimId)
            .OrderBy(e => e.Seq)
            .ToListAsync(ct);
        return new DecisionTrailSnapshot(entries, HashChainVerifier.Verify(entries).IsValid);
    }
}
