using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Integration;

namespace Warranty.Integrations.Simulated;

/// <summary>
/// Simulated notifications: an outbox row in <c>integration.notifications</c>, added to the caller's
/// unit of work. Nothing is sent in the PoC.
/// </summary>
internal sealed class SimulatedNotificationService(
    IIntegrationRepository integration, ITenantContext tenantContext, TimeProvider time) : INotificationService
{
    public Task<Guid> EnqueueAsync(Guid claimId, string template, CancellationToken ct)
    {
        var notification = Notification.Create(Guid.CreateVersion7(), tenantContext.TenantId, claimId, template, time.GetUtcNow());
        integration.AddNotification(notification);
        return Task.FromResult(notification.Id);
    }

    /// <remarks>
    /// The key is stored on the outbox row. A key already queued for the tenant (in this unit of work or saved)
    /// is reported as <see cref="NotificationEnqueueResult.Duplicate"/>; a concurrent unit of work that queues
    /// the same key first makes the unique index <c>(tenant_id, dedupe_key)</c> reject this row on save, and
    /// the unit of work drops it as already queued (research R7).
    /// </remarks>
    public async Task<NotificationEnqueueResult> EnqueueAsync(Guid claimId, string template, string dedupeKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupeKey);
        var notification = Notification.Create(
            Guid.CreateVersion7(), tenantContext.TenantId, claimId, template, time.GetUtcNow(), dedupeKey);
        return await integration.TryAddNotificationAsync(notification, ct)
            ? NotificationEnqueueResult.Queued(notification.Id)
            : NotificationEnqueueResult.Duplicate;
    }
}
