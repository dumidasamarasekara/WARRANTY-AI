using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Common;

namespace Warranty.Integrations.Simulated;

/// <summary>
/// Simulated service network: the first of the tenant's service centers in the region (by name),
/// preferring one that lists the product category among its capabilities.
/// </summary>
internal sealed class SimulatedServiceNetwork(IIntegrationRepository integration) : IServiceNetwork
{
    public async Task<ServiceCenterInfo?> FindServiceCenterAsync(Region region, string productCategory, CancellationToken ct)
    {
        var centers = await integration.GetServiceCentersAsync(region, ct);
        var category = productCategory.Trim().ToLowerInvariant();
        var center = centers.FirstOrDefault(c => c.Capabilities.Contains(category)) ?? centers.FirstOrDefault();
        return center is null ? null : new ServiceCenterInfo(center.Id, center.Name, center.Region);
    }

    public async Task<IReadOnlyList<EligibleServiceCenter>> ListEligibleAsync(Region region, string productCategory, CancellationToken ct)
    {
        var centers = await integration.GetServiceCentersAsync(region, ct);
        var category = productCategory.Trim().ToLowerInvariant();
        return centers
            .Where(c => c.IsActive)
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.Id)
            .Select(c => new EligibleServiceCenter(
                c.Id, c.Name, c.Region, c.Address, c.OpeningHours, c.Contact, c.Priority, c.Capabilities.Contains(category)))
            .ToArray();
    }
}
