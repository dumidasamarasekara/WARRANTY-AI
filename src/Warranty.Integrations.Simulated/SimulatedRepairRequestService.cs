using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Integration;

namespace Warranty.Integrations.Simulated;

/// <summary>
/// Simulated repair requests: a row in <c>integration.repair_requests</c>, added to the caller's unit
/// of work (the ActionExecutor commits it with the claim's final state). Nothing leaves the system.
/// </summary>
internal sealed class SimulatedRepairRequestService(
    IIntegrationRepository integration, ITenantContext tenantContext, TimeProvider time) : IRepairRequestService
{
    public Task<Guid> CreateAsync(Guid claimId, Guid serviceCenterId, CancellationToken ct)
    {
        var request = RepairRequest.Create(Guid.CreateVersion7(), tenantContext.TenantId, claimId, serviceCenterId, time.GetUtcNow());
        integration.AddRepairRequest(request);
        return Task.FromResult(request.Id);
    }
}
