using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Actions;
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

    public Task<RecordRemedyDecisionResult> ExecuteAsync(RecordRemedyDecisionCommand command, CancellationToken ct)
        => throw new NotImplementedException(
            $"Pending T227 ({tenant}, {claims}, {remedies}, {repairs}, {securityEvents}, {actions}, {command}, {ct}).");
}
