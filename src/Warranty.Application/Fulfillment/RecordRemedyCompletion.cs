using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Fulfillment;

namespace Warranty.Application.Fulfillment;

/// <summary>
/// A staff record that a Replacement or Refund remedy was carried out (002 contracts, <c>RemedyCompletionRequest</c>)
/// with the <c>If-Match</c> value (the remedy ETag) it was sent with. Fields are nullable so that a missing one is a
/// 400 naming it. The staff member is the principal of <see cref="ITenantContext"/>, never a request field.
/// </summary>
public sealed record RecordRemedyCompletionCommand(
    Guid ClaimId,
    string? IfMatch,
    RemedyType? Remedy,
    DateOnly? CompletedOn,
    string? Reference = null);

/// <summary>Outcome of <see cref="RecordRemedyCompletion.ExecuteAsync"/> (plan.md error mapping).</summary>
public abstract record RecordRemedyCompletionResult
{
    private RecordRemedyCompletionResult()
    {
    }

    /// <summary>201: the completion was recorded and the remedy is <see cref="RemedyState.Completed"/>.</summary>
    public sealed record Recorded(ClaimRemedy Remedy, RemedyCompletion Completion) : RecordRemedyCompletionResult;

    /// <summary>404: no such claim in the current tenant.</summary>
    public sealed record NotFound : RecordRemedyCompletionResult;

    /// <summary>403: the principal is neither a claims agent nor a claims reviewer.</summary>
    public sealed record Forbidden(string Detail) : RecordRemedyCompletionResult;

    /// <summary>412: <c>If-Match</c> is missing or does not equal the remedy's current ETag.</summary>
    public sealed record PreconditionFailed(string Detail) : RecordRemedyCompletionResult;

    /// <summary>
    /// 409: the claim has no remedy, the remedy is not <see cref="RemedyState.InProgress"/>, the requested remedy is
    /// not the current one, or another request changed the remedy concurrently. <paramref name="State"/> and
    /// <paramref name="CurrentRemedy"/> are the remedy's current values when it has one.
    /// </summary>
    public sealed record Conflict(string Detail, RemedyState? State, RemedyType? CurrentRemedy) : RecordRemedyCompletionResult;

    /// <summary>400 ValidationProblem: field (<c>remedy</c>, <c>completedOn</c>, <c>reference</c>) → messages.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : RecordRemedyCompletionResult;
}

/// <summary>
/// Records that a Replacement was handed to the customer or a Refund was completed (002 FR-034, FR-035). Pending T252.
/// </summary>
public sealed class RecordRemedyCompletion(
    ITenantContext tenant,
    IClaimRepository claims,
    IRemedyRepository remedies,
    IDecisionTrailWriter trail,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    public Task<RecordRemedyCompletionResult> ExecuteAsync(RecordRemedyCompletionCommand command, CancellationToken ct)
        => throw new NotImplementedException(
            $"Pending T252 ({tenant}, {claims}, {remedies}, {trail}, {unitOfWork}, {time}, {command}, {ct}).");
}
