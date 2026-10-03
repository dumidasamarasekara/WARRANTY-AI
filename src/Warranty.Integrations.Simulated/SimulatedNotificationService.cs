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
}
