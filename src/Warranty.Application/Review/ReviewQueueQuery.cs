using System.Text.Json.Serialization;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Claims;
using Warranty.Domain.Fulfillment;

namespace Warranty.Application.Review;

/// <summary>
/// <c>ReviewQueueItem</c> (contracts/rest-api.openapi.yaml): one escalated claim (<see cref="ReviewQueueItemKind.ClaimDecision"/>)
/// or one approved claim whose remedy awaits a reviewer (<see cref="ReviewQueueItemKind.RemedyDecision"/>, 002). The AI
/// decision and confidence come from a valid recommendation only; claim value and risk level are omitted when the
/// latest run did not determine them (e.g. a product missing from the catalog). A remedy item carries no AI or risk
/// data: its <see cref="RemedyPendingReason"/> says why the remedy waits, and its escalation reasons are that reason's label.
/// </summary>
/// <param name="SubmittedByMe">The caller submitted this claim and may not decide it (separation of duties, research R29).</param>
/// <param name="EscalationReasons">Readable labels of the guardrail reason codes (data-model.md).</param>
public sealed record ReviewQueueItem(
    Guid ClaimId,
    string Reference,
    string ProductName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? ClaimValue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AiDecision? AiDecision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Confidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RiskLevel? RiskLevel,
    DateTimeOffset EscalatedAt,
    bool SubmittedByMe,
    IReadOnlyList<string> EscalationReasons,
    ReviewQueueItemKind Kind = ReviewQueueItemKind.ClaimDecision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RemedyPendingReason? RemedyPendingReason = null);

/// <summary>What a review queue item asks the reviewer to decide.</summary>
public enum ReviewQueueItemKind
{
    /// <summary>An escalated claim (<c>UnderReview</c>): approve, reject or request information.</summary>
    ClaimDecision,

    /// <summary>An approved claim whose remedy is <see cref="RemedyState.AwaitingDecision"/> (002 FR-011, FR-031, FR-032).</summary>
    RemedyDecision,
}

/// <summary>
/// The review queue of the reviewer's tenant (FR-032): every claim in <c>UnderReview</c> with the
/// latest run's recommendation, risk level and escalation reasons, and (002) every approved claim whose remedy
/// awaits a reviewer, oldest first. The escalation time of a claim item is the claim's last transition — the move to
/// <c>UnderReview</c>; that of a remedy item is the remedy's last change — the move to <c>AwaitingDecision</c>.
/// </summary>
public sealed class ReviewQueueQuery(
    ITenantContext tenant,
    IClaimRepository claims,
    ICatalogRepository catalog,
    IAdjudicationRepository adjudication,
    IRemedyRepository remedies)
{
    private const int PageSize = 100;

    public async Task<IReadOnlyList<ReviewQueueItem>> ListAsync(CancellationToken ct)
    {
        if (!tenant.IsResolved)
        {
            throw new InvalidOperationException("The review queue requires the tenant of the staff user.");
        }

        var escalated = new List<Claim>();
        for (var page = 1; ; page++)
        {
            var batch = await claims.ListAsync(ClaimStatus.UnderReview, page, PageSize, ct);
            escalated.AddRange(batch.Items);
            if (batch.Items.Count < PageSize || escalated.Count >= batch.Total)
            {
                break;
            }
        }

        var items = new List<ReviewQueueItem>(escalated.Count);

        // The repository is tenant-scoped already; checking the tenant here keeps a misconfigured filter from leaking.
        foreach (var claim in escalated.Where(c => c.TenantId == tenant.TenantId && c.Status == ClaimStatus.UnderReview))
        {
            items.Add(await ToItemAsync(claim, ct));
        }

        foreach (var remedy in await remedies.ListAwaitingDecisionAsync(ct))
        {
            if (remedy.TenantId == tenant.TenantId
                && remedy.State == RemedyState.AwaitingDecision
                && await claims.GetAsync(remedy.ClaimId, ct) is { } claim
                && claim.TenantId == tenant.TenantId)
            {
                items.Add(await ToRemedyItemAsync(claim, remedy, ct));
            }
        }

        return items
            .OrderBy(i => i.EscalatedAt)
            .ThenBy(i => i.Reference, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<ReviewQueueItem> ToItemAsync(Claim claim, CancellationToken ct)
    {
        var product = claim.ProductId is { } productId ? await catalog.GetProductAsync(productId, ct) : null;
        var run = await adjudication.GetLatestRunAsync(claim.Id, ct);
        var record = run is null ? null : await adjudication.GetRunRecordAsync(run.Id, ct);
        var recommendation = record?.Recommendation is { IsValid: true } valid ? valid : null;
        var reasons = record?.Guardrails?.Reasons.Select(r => r.Label()).ToArray() ?? [];

        return new ReviewQueueItem(
            claim.Id,
            claim.Reference,
            product?.Name ?? claim.ProductModelCode,
            product?.ClaimValue,
            recommendation?.Decision,
            recommendation?.Confidence,
            record?.Risk?.Level,
            claim.UpdatedAt,
            claim.WasSubmittedBy(tenant.PrincipalId),
            reasons);
    }

    private async Task<ReviewQueueItem> ToRemedyItemAsync(Claim claim, ClaimRemedy remedy, CancellationToken ct)
    {
        var product = claim.ProductId is { } productId ? await catalog.GetProductAsync(productId, ct) : null;
        var reason = remedy.PendingReason ?? RemedyPendingReason.NoRemedyRule;
        return new ReviewQueueItem(
            claim.Id,
            claim.Reference,
            product?.Name ?? claim.ProductModelCode,
            product?.ClaimValue,
            AiDecision: null,
            Confidence: null,
            RiskLevel: null,
            remedy.UpdatedAt,
            claim.WasSubmittedBy(tenant.PrincipalId),
            [Label(reason)],
            ReviewQueueItemKind.RemedyDecision,
            reason);
    }

    /// <summary>The readable label of a remedy pending reason (002 data-model.md <c>pending_reason</c>).</summary>
    public static string Label(RemedyPendingReason reason) => reason switch
    {
        RemedyPendingReason.PolicyChoice => "The policy allows more than one remedy",
        RemedyPendingReason.NoRemedyRule => "No remedy rule applies",
        RemedyPendingReason.RepairFailedLimit => "Repair attempts limit reached",
        RemedyPendingReason.RepairNotEconomical => "Repair is not economical",
        _ => reason.ToString(),
    };
}
