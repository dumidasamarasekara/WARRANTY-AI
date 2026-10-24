using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Actions;
using Warranty.Application.Claims;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;

namespace Warranty.Application.Fulfillment;

/// <summary>
/// A reviewer's remedy decision (002 contracts, <c>RemedyDecisionRequest</c>) with the <c>If-Match</c> value
/// (the remedy ETag) it was sent with. The reviewer is the principal of <see cref="ITenantContext"/>, never a
/// request field.
/// </summary>
public sealed record RecordRemedyDecisionCommand(Guid ClaimId, string? IfMatch, RemedyType Remedy, string? Reason);

/// <summary>Outcome of <see cref="RecordRemedyDecision.ExecuteAsync"/> (mirrors the review decision results).</summary>
public abstract record RecordRemedyDecisionResult
{
    private RecordRemedyDecisionResult()
    {
    }

    /// <summary>201: the remedy was decided and its side effects applied.</summary>
    public sealed record Recorded(ClaimRemedy Remedy) : RecordRemedyDecisionResult;

    /// <summary>404: no such claim in the current tenant.</summary>
    public sealed record NotFound : RecordRemedyDecisionResult;

    /// <summary>403: the reviewer submitted the claim; a <c>SELF_REVIEW_REFUSED</c> event was recorded.</summary>
    public sealed record SelfReviewRefused(string Detail) : RecordRemedyDecisionResult;

    /// <summary>412: <c>If-Match</c> is missing or does not equal the remedy's current ETag.</summary>
    public sealed record PreconditionFailed(string Detail) : RecordRemedyDecisionResult;

    /// <summary>409: the remedy cannot be decided now, or another request changed it concurrently.</summary>
    public sealed record Conflict(string Detail) : RecordRemedyDecisionResult;

    /// <summary>400 ValidationProblem: field (<c>remedy</c>, <c>reason</c>) → messages.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : RecordRemedyDecisionResult;
}

/// <summary>
/// Records a reviewer's remedy decision or change (002 FR-002, FR-011, FR-013, FR-015, research R4). In order:
/// the claim must be visible in the tenant (404); a reviewer may not decide the remedy of a claim they
/// submitted (403, a <c>SELF_REVIEW_REFUSED</c> security event); <c>If-Match</c> must equal the remedy's row
/// version (412); the remedy must be awaiting a decision, or in progress before hand-over or completion (409);
/// the remedy must be one of its allowed remedies (400 naming them); a reason of 10–1000 characters is required
/// for every decision except the first decision of a policy choice (400). The decision is then applied through
/// the <see cref="IActionExecutor"/> (T227: <c>ExecuteRemedyDecisionAsync</c>); a concurrent change on save is a 409.
/// </summary>
public sealed class RecordRemedyDecision(
    ITenantContext tenant,
    IClaimRepository claims,
    IRemedyRepository remedies,
    IRepairRepository repairs,
    ISecurityEventWriter securityEvents,
    IActionExecutor actions)
{
    public const string SelfReviewDetail = "You submitted this claim; another reviewer must decide its remedy";

    public const string NoRemedyDetail = "This claim has no remedy to decide.";

    public const string ConcurrentChangeDetail = "The remedy was changed by another request; reload the claim and decide again.";

    public const string PreconditionDetail = "The remedy has changed since it was loaded; reload the claim and decide again.";

    public const string MissingIfMatchDetail = "An If-Match header with the remedy's ETag is required.";

    public async Task<RecordRemedyDecisionResult> ExecuteAsync(RecordRemedyDecisionCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!tenant.IsResolved || tenant.IsSystem)
        {
            throw new InvalidOperationException("A remedy decision requires a staff principal of a resolved tenant.");
        }

        var claim = await claims.GetAsync(command.ClaimId, ct);
        if (claim is null || claim.TenantId != tenant.TenantId)
        {
            return new RecordRemedyDecisionResult.NotFound();
        }

        if (claim.WasSubmittedBy(tenant.PrincipalId))
        {
            await securityEvents.RecordAsync(
                SecurityEventKind.SelfReviewRefused, tenant.PrincipalName, claim.Id.ToString(),
                new { reference = claim.Reference, remedy = command.Remedy.ToString() }, ct);
            return new RecordRemedyDecisionResult.SelfReviewRefused(SelfReviewDetail);
        }

        var remedy = await remedies.GetAsync(claim.Id, ct);
        if (remedy is null || remedy.TenantId != tenant.TenantId)
        {
            return new RecordRemedyDecisionResult.Conflict(NoRemedyDetail);
        }

        if (!ClaimETag.TryParse(command.IfMatch, out var expected))
        {
            return new RecordRemedyDecisionResult.PreconditionFailed(MissingIfMatchDetail);
        }

        // A change between this read and the save is still caught: the save checks the row version the remedy was loaded with (409).
        if (await remedies.GetRowVersionAsync(claim.Id, ct) != expected)
        {
            return new RecordRemedyDecisionResult.PreconditionFailed(PreconditionDetail);
        }

        var repair = remedy.Remedy == RemedyType.Repair ? await repairs.GetByClaimAsync(claim.Id, ct) : null;
        if (RemedyDecisionRules.WhyNotDecidable(remedy, repair, command.Remedy) is { } notDecidable)
        {
            return new RecordRemedyDecisionResult.Conflict(notDecidable);
        }

        if (!remedy.AllowedRemedies.Contains(command.Remedy))
        {
            return Invalid("remedy", remedy.AllowedRemedies.Count == 0
                ? $"The policy allows no remedy for this claim; {command.Remedy} cannot be chosen."
                : $"{command.Remedy} is not allowed by the policy; allowed remedies: {string.Join(", ", remedy.AllowedRemedies)}.");
        }

        var required = RemedyDecisionRules.RequiresReason(await remedies.GetDecisionsAsync(claim.Id, ct));
        if (!RemedyDecisionRules.IsValidReason(command.Reason, required))
        {
            return Invalid("reason", required
                ? $"A reason of {RemedyDecision.MinReasonLength}–{RemedyDecision.MaxReasonLength} characters is required to change the remedy."
                : $"A reason must be {RemedyDecision.MinReasonLength}–{RemedyDecision.MaxReasonLength} characters.");
        }

        var decision = new ReviewerRemedyDecision(
            claim.TenantId, claim.Id, tenant.PrincipalId, tenant.PrincipalName, command.Remedy,
            string.IsNullOrWhiteSpace(command.Reason) ? null : command.Reason.Trim());
        try
        {
            await actions.ExecuteRemedyDecisionAsync(decision, ct);
        }
        catch (ConcurrencyConflictException)
        {
            return new RecordRemedyDecisionResult.Conflict(ConcurrentChangeDetail);
        }
        catch (InvalidClaimTransitionException ex)
        {
            return new RecordRemedyDecisionResult.Conflict(ex.Message);
        }

        return new RecordRemedyDecisionResult.Recorded(remedy);
    }

    private static RecordRemedyDecisionResult.Invalid Invalid(string field, string message)
        => new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}
