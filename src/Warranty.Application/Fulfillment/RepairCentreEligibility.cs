using Warranty.Application.Abstractions.Integrations;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.Application.Fulfillment;

/// <summary>
/// The repair centres staff may choose for a claim (FR-018, FR-019): the active centres of the tenant that serve
/// the claim's region and product category, ordered by priority then name (the first is the suggestion); when none
/// serves them, every active centre of the tenant, with <see cref="EligibleServiceCenter.ServesClaim"/> false. The
/// centres come from the tenant-scoped <see cref="IServiceNetwork"/>, so another tenant's centre is never listed.
/// </summary>
public static class RepairCentreEligibility
{
    public static async Task<IReadOnlyList<EligibleServiceCenter>> ListAsync(
        IServiceNetwork network, Claim claim, Product? product, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(claim);
        var category = product?.Category ?? string.Empty;

        if (claim.Region is { } region)
        {
            var serving = (await network.ListEligibleAsync(region, category, ServiceCenterPurpose.Repair, ct)).Where(c => c.ServesClaim).ToList();
            if (serving.Count > 0)
            {
                return serving;
            }
        }

        var all = new List<EligibleServiceCenter>();
        foreach (var anyRegion in Enum.GetValues<Region>())
        {
            all.AddRange((await network.ListEligibleAsync(anyRegion, category, ServiceCenterPurpose.Repair, ct)).Select(c => c with { ServesClaim = false }));
        }

        return all
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.Id)
            .ToList();
    }
}
