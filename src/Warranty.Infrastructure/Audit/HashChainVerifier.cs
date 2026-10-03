using Microsoft.EntityFrameworkCore;
using Warranty.Domain.Audit;
using Warranty.Infrastructure.Persistence;

namespace Warranty.Infrastructure.Audit;

/// <summary>Outcome of re-verifying one claim's trail; <see cref="FirstBrokenSeq"/> is the first entry that does not check out.</summary>
public sealed record HashChainVerification(bool IsValid, int EntryCount, int? FirstBrokenSeq, string? Reason)
{
    public static HashChainVerification Valid(int entryCount) => new(true, entryCount, null, null);
}

/// <summary>Recomputes a claim's decision trail hash chain (FR-039) and reports the first broken <c>seq</c>.</summary>
public sealed class HashChainVerifier(WarrantyDbContext db)
{
    public async Task<HashChainVerification> VerifyAsync(Guid claimId, CancellationToken ct)
    {
        var entries = await db.DecisionTrailEntries.AsNoTracking()
            .Where(e => e.ClaimId == claimId)
            .OrderBy(e => e.Seq)
            .ToListAsync(ct);
        return Verify(entries);
    }

    /// <summary>Checks gap-free sequence from 1, the prev-hash links and every entry's hash.</summary>
    public static HashChainVerification Verify(IReadOnlyList<DecisionTrailEntry> entries)
    {
        var prevHash = TrailHash.Genesis;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Seq != i + 1)
            {
                return Broken(i + 1, $"expected seq {i + 1}, found {entry.Seq}");
            }

            if (entry.PrevHash != prevHash)
            {
                return Broken(entry.Seq, "prev_hash does not match the previous entry's hash");
            }

            var expected = TrailHash.Compute(
                prevHash, entry.TenantId, entry.ClaimId, entry.Seq, entry.OccurredAt, entry.Step, entry.Actor, entry.Summary,
                CanonicalJson.Canonicalize(entry.PayloadJson), entry.CorrelationId);
            if (entry.Hash != expected)
            {
                return Broken(entry.Seq, "hash does not match the entry's content");
            }

            prevHash = entry.Hash;
        }

        return HashChainVerification.Valid(entries.Count);

        HashChainVerification Broken(int seq, string reason) => new(false, entries.Count, seq, reason);
    }
}
