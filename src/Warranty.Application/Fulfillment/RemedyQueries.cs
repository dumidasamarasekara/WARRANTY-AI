using System.Text.Json.Serialization;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Claims;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Remedies;

namespace Warranty.Application.Fulfillment;

/// <summary>
/// <c>RemedyView</c> (contracts/rest-api.openapi.yaml): a claim's current remedy, its policy basis, the
/// completion record and the decision history, with the remedy's own ETag (its <c>xmin</c> row version) for
/// <c>If-Match</c>.
/// </summary>
public sealed record RemedyView(
    RemedyState State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RemedyType? Remedy,
    RemedyScenario Scenario,
    IReadOnlyList<RemedyType> AllowedRemedies,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RemedyType? RecommendedRemedy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RemedyPendingReason? PendingReason,
    RemedyPolicyVersionView PolicyVersion,
    string TableRow,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RemedyCompletionView? Completion,
    IReadOnlyList<RemedyHistoryEntry> History,
    [property: JsonPropertyName("etag")] string ETag);

/// <summary>The policy version the remedy was evaluated against.</summary>
public sealed record RemedyPolicyVersionView(string PolicyCode, int Version);

/// <summary>The Replacement or Refund completion record (a completed Repair is the repair's <c>Collected</c> status).</summary>
public sealed record RemedyCompletionView(
    DateOnly CompletedOn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reference,
    string RecordedBy);

/// <summary>One row of the remedy history (<c>claims.remedy_decisions</c>), in sequence order.</summary>
public sealed record RemedyHistoryEntry(
    int Seq,
    RemedyDecisionKind Kind,
    RemedyScenario Scenario,
    IReadOnlyList<RemedyType> AllowedRemedies,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RemedyType? Remedy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RemedyDecidedBy? DecidedBy,
    string Actor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
    DateTimeOffset CreatedAt);

/// <summary>
/// <c>RemedyPreview</c>: what approving the claim would set, computed by the same evaluator as the remedy
/// itself (research R2). <see cref="NeedsChoice"/> is true when no remedy would be set automatically, so the
/// reviewer must pick one of <see cref="AllowedRemedies"/>.
/// </summary>
public sealed record RemedyPreview(
    RemedyScenario Scenario,
    IReadOnlyList<RemedyType> AllowedRemedies,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RemedyType? RecommendedRemedy,
    bool NeedsChoice,
    string TableRow);

/// <summary>
/// Remedy read models of the current tenant's claims (002 contracts). Every lookup goes through the
/// tenant-scoped repositories. The preview and the remedy set at approval use the same deterministic
/// <see cref="RemedyPolicyEvaluator"/> and the same 001 version-selection rule, so the preview cannot
/// promise a remedy the approval would not set.
/// </summary>
public sealed class RemedyQueries(
    IRemedyRepository remedies,
    IPolicyRepository policies,
    IReviewRepository reviews,
    RemedyPolicyEvaluator evaluator)
{
    /// <summary>The claim's remedy with its history and ETag, or null when no remedy has been set.</summary>
    public async Task<RemedyView?> GetRemedyAsync(Guid claimId, CancellationToken ct)
    {
        var remedy = await remedies.GetAsync(claimId, ct);
        if (remedy is null || await remedies.GetRowVersionAsync(claimId, ct) is not { } rowVersion)
        {
            return null;
        }

        var decisions = await remedies.GetDecisionsAsync(claimId, ct);
        var completion = await remedies.GetCompletionAsync(claimId, ct);
        var version = await policies.GetVersionAsync(remedy.PolicyVersionId, ct);
        var policy = version is null ? null : await policies.GetPolicyAsync(version.PolicyId, ct);

        // Actors are stored as subs; a reviewer who decided on this claim is shown by name.
        var names = (await reviews.GetForClaimAsync(claimId, ct))
            .GroupBy(d => d.ReviewerSub, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().ReviewerName, StringComparer.Ordinal);
        string Name(string actor) => names.GetValueOrDefault(actor) ?? actor;

        var latest = decisions.MaxBy(d => d.Seq);
        var history = decisions
            .OrderBy(d => d.Seq)
            .Select(d => new RemedyHistoryEntry(
                d.Seq, d.Kind, d.Scenario, d.AllowedRemedies, d.Remedy, d.DecidedBy, Name(d.Actor), d.Reason, d.CreatedAt))
            .ToList();

        return new RemedyView(
            remedy.State,
            remedy.Remedy,
            remedy.Scenario,
            remedy.AllowedRemedies,
            remedy.AllowedRemedies.Count == 1 ? remedy.AllowedRemedies[0] : null,
            remedy.PendingReason,
            new RemedyPolicyVersionView(policy?.Code ?? string.Empty, version?.Version ?? 0),
            latest?.TableRow ?? RemedyPolicyEvaluator.TableRowOf(remedy.Scenario),
            completion is null ? null : new RemedyCompletionView(completion.CompletedOn, completion.Reference, Name(completion.RecordedBy)),
            history,
            ClaimQueries.FormatETag(rowVersion));
    }

    /// <summary>
    /// What approving <paramref name="claim"/> would set for <paramref name="scenario"/> (research R13: an
    /// approval without a confirmed protection plan is <see cref="RemedyScenario.CoveredDefect"/>). Without
    /// exactly one applicable policy version there are no rules, so a reviewer must decide.
    /// </summary>
    public async Task<RemedyPreview> PreviewAsync(
        Claim claim, Product? product, RemedyScenario scenario, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var version = await ApplicableVersionAsync(claim, product, ct);
        var evaluation = evaluator.Evaluate(version?.RemedyRules, scenario, claimValue: product?.ClaimValue);
        return ToPreview(evaluation);
    }

    /// <summary>The evaluator output as a preview: the recommendation is the single allowed remedy.</summary>
    public static RemedyPreview ToPreview(RemedyEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        return new RemedyPreview(
            evaluation.Scenario,
            evaluation.AllowedRemedies,
            evaluation.SingleRemedy,
            evaluation.AutomaticRemedy is null,
            evaluation.TableRow);
    }

    /// <summary>The 001 version-selection rule: the one version in effect for the claim's region, purchase date and category.</summary>
    private async Task<PolicyVersion?> ApplicableVersionAsync(Claim claim, Product? product, CancellationToken ct)
        => ApplicableVersion(await policies.GetVersionsAsync(ct), claim, product);

    /// <summary>
    /// The 001 version-selection rule over <paramref name="versions"/>: the one version in effect for the claim's region,
    /// purchase date and product category; null when the claim has no region or not exactly one version applies.
    /// </summary>
    public static PolicyVersion? ApplicableVersion(IEnumerable<PolicyVersion> versions, Claim claim, Product? product)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.Region is not { } region)
        {
            return null;
        }

        var applicable = versions.Where(v => v.AppliesTo(claim.PurchaseDate, region, product?.Category)).Take(2).ToList();
        return applicable.Count == 1 ? applicable[0] : null;
    }
}
