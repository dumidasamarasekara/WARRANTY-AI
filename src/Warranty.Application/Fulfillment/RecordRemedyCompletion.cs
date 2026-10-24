using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Claims;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
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
/// Records that a Replacement was handed to the customer or a Refund was completed (002 FR-034, FR-035). In order: the
/// principal must be a claims agent or a claims reviewer (403); the claim must be visible in the tenant (404); the
/// request must name a Replacement or Refund (a Repair completes through the <c>Collected</c> repair event), a
/// completion date not in the future and, for a Refund, the refund reference of at most
/// <see cref="MaxReferenceLength"/> characters (400 naming the field); the claim must have a remedy (409);
/// <c>If-Match</c> must equal the remedy's row version (412); the remedy must be <see cref="RemedyState.InProgress"/>
/// with the requested remedy (409 naming the state and the current remedy). One transaction then writes the
/// <see cref="RemedyCompletion"/> row, completes the remedy and appends a <see cref="TrailStep.RemedyCompleted"/>
/// entry; a concurrent change detected on save is a 409. No money moves here (FR-035): the refund itself was
/// requested by the <c>ActionExecutor</c> when the remedy became Refund.
/// </summary>
public sealed class RecordRemedyCompletion(
    ITenantContext tenant,
    IClaimRepository claims,
    IRemedyRepository remedies,
    IDecisionTrailWriter trail,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    /// <summary>Contract <c>RemedyCompletionRequest.reference</c> <c>maxLength</c>.</summary>
    public const int MaxReferenceLength = 100;

    public const string ForbiddenDetail = "Only claims agents and claims reviewers can record a remedy completion.";

    public const string NoRemedyDetail = "This claim has no remedy to complete.";

    public const string PreconditionDetail = "The remedy has changed since it was loaded; reload it and try again.";

    public const string MissingIfMatchDetail = "An If-Match header with the remedy's ETag is required.";

    public const string ConcurrentChangeDetail = "The remedy was changed by another request; reload it and try again.";

    public async Task<RecordRemedyCompletionResult> ExecuteAsync(RecordRemedyCompletionCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!tenant.IsResolved || tenant.IsSystem)
        {
            throw new InvalidOperationException("A remedy completion requires a staff principal of a resolved tenant.");
        }

        if (!tenant.Roles.Contains(Principals.ClaimsAgentRole) && !tenant.Roles.Contains(Principals.ClaimsReviewerRole))
        {
            return new RecordRemedyCompletionResult.Forbidden(ForbiddenDetail);
        }

        var claim = await claims.GetAsync(command.ClaimId, ct);
        if (claim is null || claim.TenantId != tenant.TenantId)
        {
            return new RecordRemedyCompletionResult.NotFound();
        }

        var now = time.GetUtcNow();
        var errors = Validate(command, DateOnly.FromDateTime(now.UtcDateTime));
        if (errors.Count > 0)
        {
            return new RecordRemedyCompletionResult.Invalid(errors);
        }

        var remedy = await remedies.GetAsync(claim.Id, ct);
        if (remedy is null || remedy.TenantId != tenant.TenantId)
        {
            return new RecordRemedyCompletionResult.Conflict(NoRemedyDetail, null, null);
        }

        if (!ClaimETag.TryParse(command.IfMatch, out var expected))
        {
            return new RecordRemedyCompletionResult.PreconditionFailed(MissingIfMatchDetail);
        }

        // A change between this read and the save is still caught: the save checks the row version the remedy was loaded with (409).
        if (await remedies.GetRowVersionAsync(claim.Id, ct) != expected)
        {
            return new RecordRemedyCompletionResult.PreconditionFailed(PreconditionDetail);
        }

        var requested = command.Remedy!.Value;
        if (remedy.State != RemedyState.InProgress || remedy.Remedy != requested)
        {
            return NotCompletable(remedy, requested);
        }

        RemedyCompletion completion;
        try
        {
            completion = RemedyCompletion.Record(
                Guid.CreateVersion7(), remedy, command.CompletedOn!.Value, command.Reference, refundRequestId: null, tenant.PrincipalId, now);
            await unitOfWork.ExecuteInTransactionAsync(
                async token =>
                {
                    remedies.AddCompletion(completion);
                    remedy.Complete(requested, now);
                    await trail.AppendAsync(
                        claim.Id, TrailStep.RemedyCompleted, tenant.PrincipalId, Summary(completion),
                        new
                        {
                            remedy = requested.ToString(),
                            completedOn = completion.CompletedOn,
                            reference = completion.Reference,
                            recordedAt = now,
                        },
                        token);
                },
                ct);
        }
        catch (ConcurrencyConflictException)
        {
            return new RecordRemedyCompletionResult.Conflict(ConcurrentChangeDetail, null, null);
        }
        catch (InvalidClaimTransitionException)
        {
            return NotCompletable(remedy, requested);
        }

        return new RecordRemedyCompletionResult.Recorded(remedy, completion);
    }

    private static Dictionary<string, string[]> Validate(RecordRemedyCompletionCommand command, DateOnly today)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        switch (command.Remedy)
        {
            case null:
                errors["remedy"] = ["The remedy (Replacement or Refund) is required."];
                break;
            case RemedyType.Repair:
                errors["remedy"] = ["A Repair is completed by recording the Collected repair event, not by a completion record."];
                break;
        }

        if (command.CompletedOn is not { } completedOn)
        {
            errors["completedOn"] = ["The completion date is required."];
        }
        else if (completedOn > today)
        {
            errors["completedOn"] = ["The completion date cannot be in the future."];
        }

        var reference = command.Reference?.Trim();
        if (reference is { Length: > MaxReferenceLength })
        {
            errors["reference"] = [$"The reference must be at most {MaxReferenceLength} characters."];
        }
        else if (command.Remedy == RemedyType.Refund && string.IsNullOrEmpty(reference))
        {
            errors["reference"] = ["A refund completion needs the refund reference from the refund option."];
        }

        return errors;
    }

    private static RecordRemedyCompletionResult.Conflict NotCompletable(ClaimRemedy remedy, RemedyType requested)
    {
        var detail = remedy.State == RemedyState.InProgress
            ? $"The claim's current remedy is {remedy.Remedy}, not {requested}."
            : $"The remedy is {remedy.State}{(remedy.Remedy is { } current ? $" ({current})" : string.Empty)}; only a remedy in progress can be completed.";
        return new RecordRemedyCompletionResult.Conflict(detail, remedy.State, remedy.Remedy);
    }

    private static string Summary(RemedyCompletion completion) => completion.Remedy == RemedyType.Refund
        ? $"Refund completed on {completion.CompletedOn:yyyy-MM-dd} (reference {completion.Reference})."
        : $"Replacement handed to the customer on {completion.CompletedOn:yyyy-MM-dd}.";
}
