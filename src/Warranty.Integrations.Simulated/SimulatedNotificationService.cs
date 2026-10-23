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
    /// The key is stored on the outbox row; the unique index <c>(tenant_id, dedupe_key)</c> rejects a second
    /// row when the unit of work is saved. Translating that violation into "already queued" is done in the
    /// persistence layer (T212), so this method reports every call as queued.
    /// </remarks>
    public Task<NotificationEnqueueResult> EnqueueAsync(Guid claimId, string template, string dedupeKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupeKey);
        var notification = Notification.Create(
            Guid.CreateVersion7(), tenantContext.TenantId, claimId, template, time.GetUtcNow(), dedupeKey);
        integration.AddNotification(notification);
        return Task.FromResult(NotificationEnqueueResult.Queued(notification.Id));
    }
}
