using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Actions;
using Warranty.Application.Claims;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Review;
using Warranty.Guardrails.Rules;

namespace Warranty.Application.Review;

/// <summary>
/// A reviewer decision request (contracts/rest-api.openapi.yaml, <c>ReviewDecisionRequest</c>) with the
/// <c>If-Match</c> value it was sent with. The reviewer is the principal of <see cref="ITenantContext"/>,
/// never a request field.
/// </summary>
public sealed record RecordReviewDecisionCommand(
    Guid ClaimId,
    string? IfMatch,
    ReviewDecisionKind Decision,
    string? Justification,
    string? ClaimantExplanation,
    IReadOnlyList<RequestedItem>? RequestedItems);

/// <summary>Outcome of <see cref="RecordReviewDecision.ExecuteAsync"/>.</summary>
public abstract record RecordReviewDecisionResult
{
    private RecordReviewDecisionResult()
    {
    }

    /// <summary>201: the decision was recorded and the claim updated.</summary>
    public sealed record Recorded(ReviewDecision Decision, ClaimStatus ClaimStatus) : RecordReviewDecisionResult;

    /// <summary>404: no such claim in the current tenant.</summary>
    public sealed record NotFound : RecordReviewDecisionResult;

    /// <summary>403: the reviewer submitted the claim (research R29); a <c>SELF_REVIEW_REFUSED</c> event was recorded.</summary>
    public sealed record SelfReviewRefused(string Detail) : RecordReviewDecisionResult;

    /// <summary>412: <c>If-Match</c> is missing or does not equal the claim's current ETag.</summary>
    public sealed record PreconditionFailed(string Detail) : RecordReviewDecisionResult;

    /// <summary>409: the claim is not under review, or another decision changed it concurrently.</summary>
    public sealed record Conflict(string Detail) : RecordReviewDecisionResult;

    /// <summary>400 ValidationProblem: field (<c>justification</c>, <c>claimantExplanation</c>, <c>requestedItems</c>) → messages.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : RecordReviewDecisionResult;
}

/// <summary>
/// Records a reviewer's decision on an escalated claim (FR-034 – FR-036, research R25/R29). In order:
/// the claim must be visible in the tenant; a reviewer may not decide a claim they submitted (403, a
/// <c>SELF_REVIEW_REFUSED</c> security event, nothing else changes); <c>If-Match</c> must equal the
/// claim's row version (412); the claim must be <c>UnderReview</c> (409); the decision must satisfy
/// the field rules of <see cref="ReviewDecision.Create"/> — <c>overridesAi</c> is computed against the
/// latest run's recommendation — and its claimant explanation must pass <see cref="ClaimantTextScreen"/>
/// (400 naming the term). The decision is then applied through
/// <see cref="IActionExecutor.ExecuteReviewerDecisionAsync"/>; a concurrent change detected on save is a 409.
/// </summary>
public sealed class RecordReviewDecision(
    ITenantContext tenant,
    IClaimRepository claims,
    IAdjudicationRepository adjudication,
    ISecurityEventWriter securityEvents,
    IActionExecutor actions,
    TimeProvider time)
{
    public const string SelfReviewDetail = "You submitted this claim; another reviewer must decide it";

    public const string NotUnderReviewDetail = "This claim is not under review; it may already have been decided.";

    public const string ConcurrentChangeDetail = "This claim was changed by another request; reload it and decide again.";

    public const string PreconditionDetail = "The claim has changed since it was loaded; reload it and decide again.";

    public const string MissingIfMatchDetail = "An If-Match header with the claim's ETag is required.";

    public async Task<RecordReviewDecisionResult> ExecuteAsync(RecordReviewDecisionCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!tenant.IsResolved || tenant.IsSystem)
        {
            throw new InvalidOperationException("A review decision requires a staff principal of a resolved tenant.");
        }

        var claim = await claims.GetAsync(command.ClaimId, ct);
        if (claim is null || claim.TenantId != tenant.TenantId)
        {
            return new RecordReviewDecisionResult.NotFound();
        }

        if (claim.WasSubmittedBy(tenant.PrincipalId))
        {
            await securityEvents.RecordAsync(
                SecurityEventKind.SelfReviewRefused, tenant.PrincipalName, claim.Id.ToString(),
                new { reference = claim.Reference, decision = command.Decision.ToString() }, ct);
            return new RecordReviewDecisionResult.SelfReviewRefused(SelfReviewDetail);
        }

        if (!ClaimETag.TryParse(command.IfMatch, out var expected))
        {
            return new RecordReviewDecisionResult.PreconditionFailed(MissingIfMatchDetail);
        }

        // A change between this read and the save is still caught: the save checks the row version the claim was loaded with (409).
        if (await claims.GetRowVersionAsync(claim.Id, ct) != expected)
        {
            return new RecordReviewDecisionResult.PreconditionFailed(PreconditionDetail);
        }

        if (claim.Status != ClaimStatus.UnderReview)
        {
            return new RecordReviewDecisionResult.Conflict(NotUnderReviewDetail);
        }

        var run = await adjudication.GetLatestRunAsync(claim.Id, ct);
        if (run is null)
        {
            return new RecordReviewDecisionResult.Conflict(NotUnderReviewDetail);
        }

        var record = await adjudication.GetRunRecordAsync(run.Id, ct);
        var recommendation = record?.Recommendation is { } r && r.RunId == run.Id ? r : null;

        if (!TryNormalizeItems(command.RequestedItems, out var items))
        {
            return Invalid("requestedItems", "Each requested item needs an item code and a reason.");
        }

        ReviewDecision decision;
        try
        {
            decision = ReviewDecision.Create(
                Guid.CreateVersion7(), claim.TenantId, claim.Id, run.Id, tenant.PrincipalId, tenant.PrincipalName, command.Decision,
                command.Justification, command.ClaimantExplanation, items, recommendation, time.GetUtcNow());
        }
        catch (ReviewDecisionValidationException ex)
        {
            return Invalid(ex.Field, ex.Message);
        }

        if (decision.ClaimantExplanation is { } explanation && ClaimantTextScreen.Screen(explanation) is { IsSafe: false } screen)
        {
            return Invalid(
                "claimantExplanation",
                $"The claimant explanation must not contain risk or fraud terms or internal reference IDs; remove \"{screen.OffendingTerm}\".");
        }

        try
        {
            await actions.ExecuteReviewerDecisionAsync(decision, ct);
        }
        catch (ConcurrencyConflictException)
        {
            return new RecordReviewDecisionResult.Conflict(ConcurrentChangeDetail);
        }
        catch (InvalidClaimTransitionException)
        {
            return new RecordReviewDecisionResult.Conflict(NotUnderReviewDetail);
        }

        return new RecordReviewDecisionResult.Recorded(decision, claim.Status);
    }

    private static bool TryNormalizeItems(IReadOnlyList<RequestedItem>? requested, out RequestedItem[] items)
    {
        items = [];
        if (requested is null)
        {
            return true;
        }

        if (requested.Any(i => i is null || string.IsNullOrWhiteSpace(i.Item) || string.IsNullOrWhiteSpace(i.Reason)))
        {
            return false;
        }

        items = requested.Select(i => RequestedItem.Create(i.Item, i.Reason)).ToArray();
        return true;
    }

    private static RecordReviewDecisionResult.Invalid Invalid(string field, string message)
        => new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}
