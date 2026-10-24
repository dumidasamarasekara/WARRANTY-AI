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

/// <summary><c>ReplacementModel</c> (003 contracts): a catalog model of the tenant handed out or proposed as a replacement.</summary>
public sealed record ReplacementModelView(Guid ProductId, string ModelCode, string Name, string Category, decimal ClaimValue)
{
    public static ReplacementModelView Of(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);
        return new ReplacementModelView(product.Id, product.ModelCode, product.Name, product.Category, product.ClaimValue);
    }
}

/// <summary>The recorded faulty-product return (staff-only: the serial seen and whether it matched the claim's).</summary>
public sealed record FaultyProductReturnView(
    DateOnly ReturnedOn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SerialSeen,
    bool SerialMatches,
    bool HeldByRepairCentre);

/// <summary>The equivalent-model proposal awaiting a reviewer; <see cref="ProposedBy"/> is the staff display name.</summary>
public sealed record ReplacementProposalView(ReplacementModelView Model, string Reason, string ProposedBy, DateTimeOffset ProposedAt);

/// <summary>The latest reserved unit (it may have ended); <see cref="SerialNumber"/> is staff-only.</summary>
public sealed record ReplacementUnitView(int Seq, string ModelCode, string SerialNumber, ReplacementUnitResult Result);

/// <summary>
/// <c>ReplacementView</c> (003 contracts): the claim's replacement with its copied policy rules, active location,
/// return, pending proposal, approved model and current unit, the events allowed from its status for the caller's role
/// (they drive the staff buttons; none for an auditor), its history with staff display names, and the replacement's own
/// ETag (its <c>xmin</c> row version) for <c>If-Match</c>.
/// </summary>
public sealed record ReplacementView(
    ReplacementStatus Status,
    ReplacementProductRule ProductRule,
    ReplacementReturnRule ReturnRule,
    int MinimumCoverMonths,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RepairCentreView? ActiveLocation,
    ReturnState ReturnState,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FaultyProductReturnView? FaultyProductReturn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementProposalView? PendingProposal,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementModelView? ApprovedModel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementUnitView? CurrentUnit,
    IReadOnlyList<ReplacementEventType> AllowedEvents,
    IReadOnlyList<ReplacementEventView> Events,
    [property: JsonPropertyName("etag")] string ETag);

/// <summary>One replacement event in sequence order; <see cref="Actor"/> is the staff member's display name, or "system".</summary>
public sealed record ReplacementEventView(
    int Seq,
    ReplacementEventType Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementStatus? FromStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplacementStatus? ToStatus,
    DateTimeOffset ReportedAt,
    DateTimeOffset RecordedAt,
    ReportSource Source,
    string Actor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CorrectsSeq);

/// <summary>
/// Replacement read models of the current tenant's claims (003 contracts): the <see cref="ReplacementView"/> of the
/// claim detail and the eligible replacement locations of a claim. Every lookup goes through the tenant-scoped
/// repositories and the tenant-scoped <see cref="IServiceNetwork"/>, so another tenant's claim or location reads as
/// missing. The caller's role (<see cref="RecordReplacementEvent.StaffRoleOf"/>, reviewer before agent) decides the
/// allowed events; a caller without a recording role (an auditor) gets none.
/// </summary>
public sealed class ReplacementQueries(
    ITenantContext tenant,
    IClaimRepository claims,
    ICatalogRepository catalog,
    IReplacementRepository replacements,
    IReviewRepository reviews,
    IIntegrationRepository integration,
    IServiceNetwork network)
{
    /// <summary>
    /// The eligible replacement locations of the claim (003 FR-008, FR-009): active centres of the tenant that issue
    /// replacements, those serving the claim's region and product category first (the first = suggested), else every
    /// replacement location with <c>servesClaim</c> false; null when the claim is not visible.
    /// </summary>
    public async Task<IReadOnlyList<RepairCentreView>?> GetReplacementLocationsAsync(Guid claimId, CancellationToken ct)
    {
        EnsureTenant();
        var claim = await claims.GetAsync(claimId, ct);
        if (claim is null || claim.TenantId != tenant.TenantId)
        {
            return null;
        }

        var product = claim.ProductId is { } productId ? await catalog.GetProductAsync(productId, ct) : null;
        var locations = await RepairCentreEligibility.ListAsync(network, claim, product, ServiceCenterPurpose.Replacement, ct);
        return locations
            .Select((c, index) => new RepairCentreView(
                c.Id, c.Name, c.Region, c.Address ?? string.Empty, c.OpeningHours ?? string.Empty, c.Contact,
                Suggested: index == 0 && c.ServesClaim, c.ServesClaim))
            .ToList();
    }

    /// <summary>The claim's replacement view, or null when the claim is not visible or has no replacement.</summary>
    public async Task<ReplacementView?> GetReplacementAsync(Guid claimId, CancellationToken ct)
    {
        EnsureTenant();
        var claim = await claims.GetAsync(claimId, ct);
        if (claim is null || claim.TenantId != tenant.TenantId)
        {
            return null;
        }

        var product = claim.ProductId is { } productId ? await catalog.GetProductAsync(productId, ct) : null;
        return await GetReplacementAsync(claim, product, ct);
    }

    /// <summary>The replacement view of an already loaded claim of the current tenant, or null when it has no replacement.</summary>
    public async Task<ReplacementView?> GetReplacementAsync(Claim claim, Product? product, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var replacement = await replacements.GetByClaimAsync(claim.Id, ct);
        if (replacement is null || await replacements.GetRowVersionAsync(claim.Id, ct) is not { } rowVersion)
        {
            return null;
        }

        // Actors are stored as subs; the display name comes from the event's own payload, else from another event of the same
        // staff member (for the proposer of a pending proposal), else from a review decision on the claim, else the sub.
        var names = (await reviews.GetForClaimAsync(claim.Id, ct))
            .GroupBy(d => d.ReviewerSub, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().ReviewerName, StringComparer.Ordinal);
        foreach (var replacementEvent in replacement.Events.OrderBy(e => e.Seq))
        {
            if (replacementEvent.Actor != ReplacementEvent.SystemActor && PayloadActorName(replacementEvent) is { } name)
            {
                names[replacementEvent.Actor] = name;
            }
        }

        var seqById = replacement.Events.Where(e => e.Id != 0).ToDictionary(e => e.Id, e => e.Seq);
        var products = new Dictionary<Guid, Product?>();
        if (product is not null)
        {
            products[product.Id] = product;
        }

        var faultyReturn = replacement.Return;
        var proposal = replacement.PendingProposal;
        var proposedModel = proposal is null ? null : await ProductAsync(proposal.ProductId, products, ct);
        var approvedModel = replacement.ApprovedProductId is { } approvedId ? await ProductAsync(approvedId, products, ct) : null;
        var unit = replacement.CurrentUnit;
        var unitModel = unit is null ? null : await ProductAsync(unit.ProductId, products, ct);
        var location = replacement.ActiveLocation;

        return new ReplacementView(
            replacement.Status,
            replacement.ProductRule,
            replacement.ReturnRule,
            replacement.MinimumCoverMonths,
            location is null ? null : await LocationAsync(claim, product, location, ct),
            replacement.ReturnState,
            faultyReturn is null
                ? null
                : new FaultyProductReturnView(
                    faultyReturn.ReturnedOn, faultyReturn.SerialSeen, faultyReturn.SerialMatches, faultyReturn.IsHeldByRepairCentre),
            proposal is null || proposedModel is null
                ? null
                : new ReplacementProposalView(
                    ReplacementModelView.Of(proposedModel), proposal.Reason, ActorName(proposal.ProposedBy, names), proposal.ProposedAt),
            approvedModel is null ? null : ReplacementModelView.Of(approvedModel),
            unit is null ? null : new ReplacementUnitView(unit.Seq, unitModel?.ModelCode ?? claim.ProductModelCode, unit.SerialNumber, unit.Result),
            RecordReplacementEvent.StaffRoleOf(tenant.Roles) is { } role ? replacement.AllowedEvents(role) : [],
            replacement.Events
                .OrderBy(e => e.Seq)
                .Select(e => new ReplacementEventView(
                    e.Seq,
                    e.Type,
                    e.FromStatus,
                    e.ToStatus,
                    e.ReportedAt,
                    e.RecordedAt,
                    e.Source,
                    e.Actor == ReplacementEvent.SystemActor ? ReplacementEvent.SystemActor : PayloadActorName(e) ?? ActorName(e.Actor, names),
                    e.Note,
                    e.CorrectsEventId is { } corrected && seqById.TryGetValue(corrected, out var seq) ? seq : null))
                .ToList(),
            ClaimQueries.FormatETag(rowVersion));
    }

    private async Task<Product?> ProductAsync(Guid productId, Dictionary<Guid, Product?> products, CancellationToken ct)
    {
        if (!products.TryGetValue(productId, out var product))
        {
            product = await catalog.GetProductAsync(productId, ct);
            products[productId] = product;
        }

        return product;
    }

    /// <summary>The active location's centre as stored (it may since have become inactive), or null when it is not visible.</summary>
    private async Task<RepairCentreView?> LocationAsync(Claim claim, Product? product, ReplacementLocation location, CancellationToken ct)
    {
        var regions = claim.Region is { } region
            ? new[] { region }.Concat(Enum.GetValues<Region>().Where(r => r != region))
            : Enum.GetValues<Region>();
        ServiceCenter? centre = null;
        foreach (var candidate in regions)
        {
            centre = (await integration.GetServiceCentersAsync(candidate, ct)).FirstOrDefault(c => c.Id == location.ServiceCenterId);
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
            location.Assignment == LocationAssignment.Suggested, servesClaim);
    }

    private static string ActorName(string actor, IReadOnlyDictionary<string, string> names)
        => actor == ReplacementEvent.SystemActor ? ReplacementEvent.SystemActor : names.GetValueOrDefault(actor) ?? actor;

    private static string? PayloadActorName(ReplacementEvent replacementEvent)
    {
        try
        {
            using var payload = JsonDocument.Parse(replacementEvent.PayloadJson);
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

        return null;
    }

    private void EnsureTenant()
    {
        if (!tenant.IsResolved)
        {
            throw new InvalidOperationException("Replacement queries require the tenant of the staff user.");
        }
    }
}
