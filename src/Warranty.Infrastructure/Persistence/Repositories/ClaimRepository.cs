using Microsoft.EntityFrameworkCore;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;

namespace Warranty.Infrastructure.Persistence.Repositories;

internal sealed class ClaimRepository(WarrantyDbContext db) : IClaimRepository
{
    /// <summary>Another claim finalized this many days before the claim date is still a duplicate (research R25).</summary>
    public const int DuplicateClaimWindowDays = 90;

    public const int MaxPageSize = 100;

    /// <summary>
    /// Photo damage types that show accidental damage — the types research R26 maps to
    /// <c>ACCIDENTAL_DAMAGE</c> (keep in line with the guardrails' <c>ExclusionEvidenceMap</c>). An
    /// approved claim counts as an accidental-damage claim when a photo analysis reported one of them.
    /// </summary>
    internal static readonly string[] AccidentalDamageTypes = ["CRACKED_SCREEN", "DENTS_OR_IMPACT"];

    public void Add(Claim claim) => db.Claims.Add(claim);

    public Task<Claim?> GetAsync(Guid claimId, CancellationToken ct)
        => db.Claims.SingleOrDefaultAsync(c => c.Id == claimId, ct);

    public Task<Claim?> FindByReferenceAsync(string reference, CancellationToken ct)
    {
        var normalized = ClaimReference.Normalize(reference);
        return ClaimReference.IsValid(normalized)
            ? db.Claims.SingleOrDefaultAsync(c => c.Reference == normalized, ct)
            : Task.FromResult<Claim?>(null);
    }

    public void AddEvidence(ClaimEvidence evidence) => db.ClaimEvidence.Add(evidence);

    public async Task<IReadOnlyList<ClaimEvidence>> GetEvidenceAsync(Guid claimId, CancellationToken ct)
        => await db.ClaimEvidence
            .Where(e => e.ClaimId == claimId)
            .OrderBy(e => e.Round).ThenBy(e => e.UploadedAt)
            .ToListAsync(ct);

    public Task<ClaimEvidence?> GetEvidenceItemAsync(Guid claimId, Guid evidenceId, CancellationToken ct)
        => db.ClaimEvidence.SingleOrDefaultAsync(e => e.ClaimId == claimId && e.Id == evidenceId, ct);

    public async Task<ClaimHistoryCounts> GetHistoryCountsAsync(
        Guid claimId, string serialNumber, DateOnly claimDate, IReadOnlyCollection<string> evidenceHashes, CancellationToken ct)
    {
        var serial = ProductSerial.NormalizeSerial(serialNumber);
        var windowStart = new DateTimeOffset(claimDate.AddDays(-DuplicateClaimWindowDays).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var otherClaimsForSerial = db.Claims.Where(c => c.Id != claimId && c.SerialNumber == serial);

        // Not final, or finalized within the window (a claim finalized after this claim date was open when it was made).
        var duplicates = await otherClaimsForSerial.CountAsync(
            c => (c.Status != ClaimStatus.Approved && c.Status != ClaimStatus.Rejected) || c.FinalizedAt >= windowStart, ct);

        // FromSql composes with the tenant query filter; RLS applies as well.
        var priorApprovedAccidental = await db.Claims
            .FromSql($"""
                SELECT c.* FROM claims.claims c
                WHERE EXISTS (
                    SELECT 1
                    FROM adjudication.adjudication_runs r
                    JOIN adjudication.evidence_findings f ON f.tenant_id = r.tenant_id AND f.run_id = r.id
                    WHERE r.tenant_id = c.tenant_id AND r.claim_id = c.id
                      AND f.kind = 'PhotoAnalysis'
                      AND jsonb_exists_any(f.result -> 'damageTypes', {AccidentalDamageTypes}))
                """)
            .CountAsync(c => c.Id != claimId && c.SerialNumber == serial && c.FinalOutcome == FinalOutcome.Approved, ct);

        var hashes = evidenceHashes.Select(h => h.ToLowerInvariant()).Distinct().ToArray();
        var evidenceReuse = hashes.Length == 0
            ? 0
            : await db.ClaimEvidence.CountAsync(e => e.ClaimId != claimId && hashes.Contains(e.Sha256), ct);

        return new ClaimHistoryCounts(duplicates, priorApprovedAccidental, evidenceReuse);
    }

    public async Task<ClaimPage> ListAsync(ClaimStatus? status, int page, int pageSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaxPageSize);

        var query = db.Claims.AsNoTracking();
        if (status is { } s)
        {
            query = query.Where(c => c.Status == s);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        return new ClaimPage(items, page, pageSize, total);
    }
}
