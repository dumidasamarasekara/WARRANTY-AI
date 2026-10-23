using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Common;
using Warranty.Domain.Integration;

namespace Warranty.Integrations.Simulated;

/// <summary>
/// Simulated service network over the tenant's service centers (research R8): only active centers in the
/// region count, ordered by priority (lower first), then name. The suggested center is the first one that
/// lists the product category among its capabilities, else the first active center of the region.
/// </summary>
internal sealed class SimulatedServiceNetwork(IIntegrationRepository integration) : IServiceNetwork
{
    public async Task<ServiceCenterInfo?> FindServiceCenterAsync(Region region, string productCategory, CancellationToken ct)
    {
        var centers = await ActiveCentersAsync(region, ct);
        var category = Normalize(productCategory);
        var center = centers.FirstOrDefault(c => c.Capabilities.Contains(category)) ?? centers.FirstOrDefault();
        return center is null ? null : new ServiceCenterInfo(center.Id, center.Name, center.Region);
    }

    public async Task<IReadOnlyList<EligibleServiceCenter>> ListEligibleAsync(Region region, string productCategory, CancellationToken ct)
    {
        var centers = await ActiveCentersAsync(region, ct);
        var category = Normalize(productCategory);
        return centers
            .Select(c => new EligibleServiceCenter(
                c.Id, c.Name, c.Region, c.Address, c.OpeningHours, c.Contact, c.Priority, c.Capabilities.Contains(category)))
            .ToArray();
    }

    private async Task<ServiceCenter[]> ActiveCentersAsync(Region region, CancellationToken ct)
    {
        var centers = await integration.GetServiceCentersAsync(region, ct);
        return centers
            .Where(c => c.IsActive)
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.Id)
            .ToArray();
    }

    private static string Normalize(string productCategory) => productCategory.Trim().ToLowerInvariant();
}
