using Microsoft.EntityFrameworkCore;
using Npgsql;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Common;
using Warranty.Domain.Crm;
using Warranty.Domain.Integration;
using Warranty.Infrastructure.Persistence.Configurations;

namespace Warranty.Infrastructure.Persistence.Repositories;

internal sealed class CustomerRepository(WarrantyDbContext db) : ICustomerRepository
{
    public async Task<Customer?> FindByEmailAsync(string normalizedEmail, CancellationToken ct)
        => db.Customers.Local.FirstOrDefault(c => c.Email == normalizedEmail)
           ?? await db.Customers.SingleOrDefaultAsync(c => c.Email == normalizedEmail, ct);

    public Task<Customer?> GetAsync(Guid customerId, CancellationToken ct)
        => db.Customers.SingleOrDefaultAsync(c => c.Id == customerId, ct);

    public void Add(Customer customer) => db.Customers.Add(customer);
}

internal sealed class IntegrationRepository(WarrantyDbContext db) : IIntegrationRepository
{
    public async Task<IReadOnlyList<ServiceCenter>> GetServiceCentersAsync(Region region, CancellationToken ct)
        => await db.ServiceCenters.AsNoTracking().Where(s => s.Region == region).OrderBy(s => s.Name).ThenBy(s => s.Id).ToListAsync(ct);

    public void AddRepairRequest(RepairRequest request) => db.RepairRequests.Add(request);

    public void AddNotification(Notification notification) => db.Notifications.Add(notification);

    public async Task<bool> TryAddNotificationAsync(Notification notification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.DedupeKey is { } key)
        {
            var queuedHere = db.ChangeTracker.Entries<Notification>()
                .Any(e => e.State != EntityState.Detached && e.Entity.DedupeKey == key);
            if (queuedHere || await db.Notifications.AsNoTracking().AnyAsync(n => n.DedupeKey == key, ct))
            {
                return false;
            }
        }

        db.Notifications.Add(notification);
        return true;
    }
}

/// <summary>
/// Translates a violation of <see cref="NotificationConfiguration.DedupeKeyIndex"/> into "already queued"
/// (research R7): when a concurrent unit of work saved a notification with the same dedupe key first, the
/// pending duplicate rows are dropped so the rest of the unit of work can be saved again.
/// </summary>
internal static class NotificationDeduplication
{
    public static bool IsDedupeKeyViolation(DbUpdateException exception)
        => exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: NotificationConfiguration.DedupeKeyIndex,
        };

    /// <summary>
    /// Detaches every added notification whose dedupe key is already stored for the tenant; returns false when
    /// there was none (the violation is then not one this unit of work can resolve).
    /// </summary>
    public static async Task<bool> DropAlreadyQueuedAsync(WarrantyDbContext db, CancellationToken ct)
    {
        var pending = db.ChangeTracker.Entries<Notification>()
            .Where(e => e.State == EntityState.Added && e.Entity.DedupeKey != null)
            .ToList();
        if (pending.Count == 0)
        {
            return false;
        }

        var keys = pending.Select(e => e.Entity.DedupeKey!).Distinct(StringComparer.Ordinal).ToList();
        var stored = await db.Notifications.AsNoTracking()
            .Where(n => n.DedupeKey != null && keys.Contains(n.DedupeKey))
            .Select(n => n.DedupeKey!)
            .ToListAsync(ct);

        var dropped = false;
        foreach (var entry in pending.Where(e => stored.Contains(e.Entity.DedupeKey!, StringComparer.Ordinal)))
        {
            entry.State = EntityState.Detached;
            dropped = true;
        }

        return dropped;
    }
}
