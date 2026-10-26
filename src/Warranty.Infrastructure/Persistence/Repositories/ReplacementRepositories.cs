using Microsoft.EntityFrameworkCore;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Catalog;
using Warranty.Domain.Fulfillment;
using Warranty.Infrastructure.Persistence.Configurations;

namespace Warranty.Infrastructure.Persistence.Repositories;

/// <summary>
/// Replacements of the current tenant's claims, loaded with their locations, return, proposals, units and
/// events. Replacement events live in the append-only <c>audit.replacement_events</c>: they are only ever
/// added, never updated or deleted.
/// </summary>
internal sealed class ReplacementRepository(WarrantyDbContext db) : IReplacementRepository
{
    public Task<Replacement?> GetByClaimAsync(Guid claimId, CancellationToken ct)
        => db.Replacements
            .Include(r => r.Locations.OrderBy(l => l.RecordedAt))
            .Include(r => r.Return)
            .Include(r => r.Proposals.OrderBy(p => p.ProposedAt))
            .Include(r => r.Units.OrderBy(u => u.Seq))
            .Include(r => r.Events.OrderBy(e => e.Seq))
            .AsSplitQuery()
            .SingleOrDefaultAsync(r => r.ClaimId == claimId, ct);

    public async Task<uint?> GetRowVersionAsync(Guid claimId, CancellationToken ct)
        => await db.Replacements.AsNoTracking()
            .Where(r => r.ClaimId == claimId)
            .Select(r => (uint?)EF.Property<uint>(r, ReplacementConfiguration.RowVersion))
            .SingleOrDefaultAsync(ct);

    public void Add(Replacement replacement) => db.Replacements.Add(replacement);

    public async Task<IReadOnlyList<Replacement>> ListReturnMismatchesAsync(CancellationToken ct)
        => await db.Replacements.AsNoTracking()
            .Include(r => r.Return)
            .Where(r => r.ReturnState == ReturnState.Mismatch
                && (r.Status == ReplacementStatus.AwaitingReturn
                    || r.Status == ReplacementStatus.AwaitingUnit
                    || r.Status == ReplacementStatus.ReadyForCollection))
            .OrderBy(r => r.UpdatedAt)
            .ToListAsync(ct);

    public void AddEvent(ReplacementEvent replacementEvent)
    {
        ArgumentNullException.ThrowIfNull(replacementEvent);
        var entry = db.Entry(replacementEvent);
        if (entry.State is EntityState.Detached)
        {
            db.ReplacementEvents.Add(replacementEvent);
        }
        else if (entry.State is not EntityState.Added)
        {
            throw new InvalidOperationException("Replacement events are append-only; a saved event cannot be added again.");
        }
    }
}

/// <summary>
/// Handed-out replacement units of the current tenant (<c>catalog.replacement_serials</c>). Registrations are
/// insert-only: they are read untracked and this registry has no update or delete for them. Serials are
/// compared in their stored form (<see cref="ProductSerial.NormalizeSerial"/>).
/// </summary>
internal sealed class ReplacementSerialRegistry(WarrantyDbContext db) : IReplacementSerialRegistry
{
    public Task<RegisteredReplacementSerial?> FindBySerialAsync(string serialNumber, CancellationToken ct)
    {
        var serial = ProductSerial.NormalizeSerial(serialNumber);
        return db.ReplacementSerials.AsNoTracking().SingleOrDefaultAsync(s => s.SerialNumber == serial, ct);
    }

    public async Task<IReadOnlyList<RegisteredReplacementSerial>> FindByOriginalSerialAsync(string originalSerial, CancellationToken ct)
    {
        var serial = ProductSerial.NormalizeSerial(originalSerial);
        return await db.ReplacementSerials.AsNoTracking()
            .Where(s => s.OriginalSerial == serial)
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .ToListAsync(ct);
    }

    public void Register(RegisteredReplacementSerial serial)
    {
        ArgumentNullException.ThrowIfNull(serial);
        var entry = db.Entry(serial);
        if (entry.State is EntityState.Detached)
        {
            db.ReplacementSerials.Add(serial);
        }
        else if (entry.State is not EntityState.Added)
        {
            throw new InvalidOperationException("Registered replacement serials are append-only; a saved registration cannot be added again.");
        }
    }

    public async Task<bool> IsSerialInUseAsync(string serialNumber, CancellationToken ct)
    {
        var serial = ProductSerial.NormalizeSerial(serialNumber);
        return await db.ReplacementSerials.AnyAsync(s => s.SerialNumber == serial, ct)
            || await db.Claims.AnyAsync(c => c.SerialNumber == serial, ct);
    }
}
