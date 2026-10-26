using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Remedies;
using Warranty.Infrastructure.Persistence;

namespace Warranty.MigrationService.Seeding;

/// <summary>
/// Seeds the approved claims of <c>replacement-scenarios.json</c> (003 quickstart §2, research R14) after the remedy
/// scenarios. Each claim is approved and gets its remedy through <see cref="RemedyScenarioSeeder"/>'s mechanics (a
/// re-evaluation of the claim's own policy version and a seeded-history reviewer decision for Replacement); in the same
/// transaction its <see cref="Replacement"/> is created as the <c>ActionExecutor</c> does (research R3): the replacement
/// rules of that policy version (<see cref="ReplacementRules.Default"/> when it has none), the first eligible replacement
/// location of the region (<see cref="IServiceNetwork.ListEligibleAsync"/> for <see cref="ServiceCenterPurpose.Replacement"/>)
/// with its <see cref="ReplacementEventType.LocationSuggested"/> event and a <see cref="TrailStep.ReplacementLocationAssigned"/>
/// trail entry, and the initial status of the return rule. A claim without a decided remedy (X9) keeps the Repair its policy
/// version gives and gets no replacement. Claims whose reference exists are skipped, so every start is idempotent.
/// </summary>
internal sealed class ReplacementScenarioSeeder(
    WarrantyDbContext db,
    ITenantContext tenantContext,
    IServiceNetwork serviceNetwork,
    ReplacementPolicy replacementPolicy,
    RemedyScenarioSeeder remedies)
{
    public Task SeedAsync(TenantSeedSet seed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seed);
        return remedies.SeedClaimsAsync(seed.Tenant, seed.ReplacementScenarios, "replacement scenario", CreateReplacementAsync, ct);
    }

    private async Task CreateReplacementAsync(SeededRemedy seeded, CancellationToken ct)
    {
        var claim = seeded.Claim;
        if (seeded.Remedy == RemedyType.Repair)
        {
            // A claim without a decided remedy (X9; a decided one must name Replacement) keeps its policy's Repair, with the
            // repair the remedy seeder created; its replacement comes from a reviewer's later remedy decision.
            return;
        }

        if (seeded.Remedy != RemedyType.Replacement)
        {
            throw new InvalidDataException(
                $"Replacement scenario claim {claim.Reference}: policy {seeded.Version.Id} gives {seeded.Remedy}, not Replacement.");
        }

        var rules = seeded.Version.RemedyRules?.Replacement ?? ReplacementRules.Default;
        var location = claim.Region is { } region
            ? (await serviceNetwork.ListEligibleAsync(region, seeded.Product.Category, ServiceCenterPurpose.Replacement, ct))
                .FirstOrDefault(c => c.ServesClaim)
            : null;

        // A seeded scenario has no repair history, so no repair centre holds the faulty product (research R9).
        var initialStatus = replacementPolicy.InitialStatus(rules, productHeldByRepairCentre: false);

        var replacement = Replacement.Create(
            Guid.CreateVersion7(), tenantContext.TenantId, claim.Id, seeded.Version.Id, rules.ProductRule, rules.ReturnRule,
            rules.MinimumCoverMonths, initialStatus, location?.Id, seeded.DecidedAt);
        db.Replacements.Add(replacement);
        if (location is not null)
        {
            await remedies.AppendAsync(
                claim.Id, seeded.DecidedAt, TrailStep.ReplacementLocationAssigned,
                $"Seeded history: replacement location suggested: {location.Name}.",
                new
                {
                    seededHistory = true,
                    replacementId = replacement.Id,
                    serviceCenterId = location.Id,
                    serviceCenterName = location.Name,
                    assignment = nameof(LocationAssignment.Suggested),
                    status = initialStatus.ToString(),
                },
                ct);
        }
    }
}
