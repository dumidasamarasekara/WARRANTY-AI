using System.Text.Json;
using System.Text.Json.Serialization;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Claims;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Integration;

namespace Warranty.Application.Fulfillment;

/// <summary>
/// <c>RepairCentre</c> (002 contracts): a repair centre staff can see or choose. <see cref="Contact"/> is staff-only;
/// <see cref="ServesClaim"/> says whether the centre serves the claim's region and product category.
/// </summary>
public sealed record RepairCentreView(
    Guid Id,
    string Name,
    Region Region,
    string Address,
    string OpeningHours,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Contact,
    bool Suggested,
    bool ServesClaim);

/// <summary>
/// <c>RepairView</c> (002 contracts): the claim's repair with its active centre, attempts summary, the events
/// allowed from its status (they drive the staff buttons), its history with staff display names, and the repair's
/// own ETag (its <c>xmin</c> row version) for <c>If-Match</c>.
/// </summary>
public sealed record RepairView(
    RepairStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RepairNotPossibleReason? NotPossibleReason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RepairCentreView? ActiveCentre,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateOnly? HandedOverOn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CentreReference,
    int FailedAttempts,
    int? AttemptLimit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? EconomicLimitAmount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? LatestEstimate,
    IReadOnlyList<RepairEventType> AllowedEvents,
    IReadOnlyList<RepairEventView> Events,
    [property: JsonPropertyName("etag")] string ETag);

/// <summary>One repair event in sequence order; <see cref="Actor"/> is the staff member's display name, or "system".</summary>
public sealed record RepairEventView(
    int Seq,
    RepairEventType Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RepairStatus? FromStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RepairStatus? ToStatus,
    DateTimeOffset ReportedAt,
    DateTimeOffset RecordedAt,
    ReportSource Source,
    string Actor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CorrectsSeq);

/// <summary>
/// Repair read models of the current tenant's claims (002 contracts): the <see cref="RepairView"/> of the claim
/// detail and the eligible repair centres of a claim. Every lookup goes through the tenant-scoped repositories and
/// the tenant-scoped <see cref="IServiceNetwork"/>, so another tenant's claim or centre reads as missing.
/// </summary>
public sealed class RepairQueries(
    ITenantContext tenant,
    IClaimRepository claims,
    ICatalogRepository catalog,
    IRepairRepository repairs,
    IRemedyRepository remedies,
    IPolicyRepository policies,
    IReviewRepository reviews,
    IIntegrationRepository integration,
    IServiceNetwork network)
{
    /// <summary>The eligible repair centres of the claim (first = suggested), or null when the claim is not visible.</summary>
    public async Task<IReadOnlyList<RepairCentreView>?> GetRepairCentresAsync(Guid claimId, CancellationToken ct)
    {
        EnsureTenant();
        var claim = await claims.GetAsync(claimId, ct);
        if (claim is null || claim.TenantId != tenant.TenantId)
        {
            return null;
        }

        var product = claim.ProductId is { } productId ? await catalog.GetProductAsync(productId, ct) : null;
        var centres = await RepairCentreEligibility.ListAsync(network, claim, product, ct);
        return centres
            .Select((c, index) => new RepairCentreView(
                c.Id, c.Name, c.Region, c.Address ?? string.Empty, c.OpeningHours ?? string.Empty, c.Contact,
                Suggested: index == 0 && c.ServesClaim, c.ServesClaim))
            .ToList();
    }

    /// <summary>The claim's repair view, or null when the claim is not visible or has no repair.</summary>
    public async Task<RepairView?> GetRepairAsync(Guid claimId, CancellationToken ct)
    {
        EnsureTenant();
        var claim = await claims.GetAsync(claimId, ct);
        if (claim is null || claim.TenantId != tenant.TenantId)
        {
            return null;
        }

        var product = claim.ProductId is { } productId ? await catalog.GetProductAsync(productId, ct) : null;
        return await GetRepairAsync(claim, product, ct);
    }

    /// <summary>The repair view of an already loaded claim of the current tenant, or null when it has no repair.</summary>
    public async Task<RepairView?> GetRepairAsync(Claim claim, Product? product, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var repair = await repairs.GetByClaimAsync(claim.Id, ct);
        if (repair is null || await repairs.GetRowVersionAsync(claim.Id, ct) is not { } rowVersion)
        {
            return null;
        }

        var remedy = await remedies.GetAsync(claim.Id, ct);
        var rules = remedy is null ? null : (await policies.GetVersionAsync(remedy.PolicyVersionId, ct))?.RemedyRules;
        var economicLimit = rules?.RepairNotEconomical is { } limitRule && product?.ClaimValue is { } claimValue
            ? Math.Round(claimValue * limitRule.LimitPercentOfClaimValue / 100m, 2, MidpointRounding.AwayFromZero)
            : (decimal?)null;

        // Actors are stored as subs; the display name comes from the event itself, else from a review decision on the claim.
        var reviewerNames = (await reviews.GetForClaimAsync(claim.Id, ct))
            .GroupBy(d => d.ReviewerSub, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().ReviewerName, StringComparer.Ordinal);
        var seqById = repair.Events.Where(e => e.Id != 0).ToDictionary(e => e.Id, e => e.Seq);

        var handover = repair.ActiveHandover;
        var latestEstimate = repair.CurrentAttempt?.EstimateAmount;
        return new RepairView(
            repair.Status,
            repair.NotPossibleReason,
            handover is null ? null : await CentreAsync(claim, product, handover, ct),
            handover?.HandedOverOn,
            handover?.CentreReference,
            repair.FailedAttempts,
            rules?.RepairFailed?.AfterFailedAttempts,
            economicLimit,
            latestEstimate,
            RecordRepairEvent.AllowedEvents(repair, remedy),
            repair.Events
                .OrderBy(e => e.Seq)
                .Select(e => new RepairEventView(
                    e.Seq,
                    e.Type,
                    e.FromStatus,
                    e.ToStatus,
                    e.ReportedAt,
                    e.RecordedAt,
                    e.Source,
                    ActorName(e, reviewerNames),
                    e.Note,
                    e.CorrectsEventId is { } corrected && seqById.TryGetValue(corrected, out var seq) ? seq : null))
                .ToList(),
            ClaimQueries.FormatETag(rowVersion));
    }


    /// <summary>The active hand-over's centre as stored (it may since have become inactive), or null when it is not visible.</summary>
    private async Task<RepairCentreView?> CentreAsync(Claim claim, Product? product, RepairHandover handover, CancellationToken ct)
    {
        var regions = claim.Region is { } region
            ? new[] { region }.Concat(Enum.GetValues<Region>().Where(r => r != region))
            : Enum.GetValues<Region>();
        ServiceCenter? centre = null;
        foreach (var candidate in regions)
        {
            centre = (await integration.GetServiceCentersAsync(candidate, ct)).FirstOrDefault(c => c.Id == handover.ServiceCenterId);
            if (centre is not null)
            {
                break;
            }
        }

        if (centre is null)
        {
            return null;
        }

        var category = product?.Category?.Trim().ToLowerInvariant();
        var servesClaim = centre.Region == claim.Region && category is not null && centre.Capabilities.Contains(category);
        return new RepairCentreView(
            centre.Id, centre.Name, centre.Region, centre.Address ?? string.Empty, centre.OpeningHours ?? string.Empty, centre.Contact,
            handover.Assignment == HandoverAssignment.Suggested, servesClaim);
    }

    private static string ActorName(RepairEvent repairEvent, IReadOnlyDictionary<string, string> reviewerNames)
    {
        if (repairEvent.Actor == RepairEvent.SystemActor)
        {
            return RepairEvent.SystemActor;
        }

        try
        {
            using var payload = JsonDocument.Parse(repairEvent.PayloadJson);
            if (payload.RootElement.ValueKind == JsonValueKind.Object
                && payload.RootElement.TryGetProperty("actorName", out var name)
                && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(name.GetString()))
            {
                return name.GetString()!;
            }
        }
        catch (JsonException)
        {
            // An unreadable payload falls back to the reviewer name or the sub.
        }

        return reviewerNames.GetValueOrDefault(repairEvent.Actor) ?? repairEvent.Actor;
    }

    private void EnsureTenant()
    {
        if (!tenant.IsResolved)
        {
            throw new InvalidOperationException("Repair queries require the tenant of the staff user.");
        }
    }
}
