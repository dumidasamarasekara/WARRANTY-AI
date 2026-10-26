using System.Text.Json;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Claims;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Remedies;
using Warranty.Guardrails.Rules;

namespace Warranty.Application.Fulfillment;

/// <summary>
/// A replacement update reported to staff (003 contracts, <c>ReplacementEventRequest</c>) with the <c>If-Match</c> value
/// (the replacement ETag) it was sent with. Fields are nullable so that a missing one is a 400 naming it; which fields an
/// event needs is listed on <c>ReplacementEventRequest</c>. The staff member and their role come from
/// <see cref="ITenantContext"/>, never from a request field.
/// </summary>
public sealed record RecordReplacementEventCommand(
    Guid ClaimId,
    string? IfMatch,
    ReplacementEventType? Type,
    DateTimeOffset? ReportedAt,
    ReportSource? Source,
    string? Note = null,
    Guid? LocationId = null,
    string? Reason = null,
    DateOnly? ReturnedOn = null,
    string? SerialSeen = null,
    Guid? ProductId = null,
    string? SerialNumber = null,
    DateOnly? HandedOverOn = null,
    bool? ReturnConfirmed = null,
    int? CorrectsSeq = null,
    ReplacementStatus? ToStatus = null);

/// <summary>Outcome of <see cref="RecordReplacementEvent.ExecuteAsync"/> (plan.md error mapping).</summary>
public abstract record RecordReplacementEventResult
{
    private RecordReplacementEventResult()
    {
    }

    /// <summary>201: the event was recorded; the updated replacement is returned.</summary>
    public sealed record Recorded(Replacement Replacement) : RecordReplacementEventResult;

    /// <summary>404: no such claim, or a location that is not an eligible replacement location of the tenant (<c>ACCESS_DENIED</c>).</summary>
    public sealed record NotFound : RecordReplacementEventResult;

    /// <summary>403: the caller's role may not record the event (an auditor records nothing; reviewer-only events by an agent).</summary>
    public sealed record Forbidden(string Detail) : RecordReplacementEventResult;

    /// <summary>
    /// 403: a reviewer-only event (<c>ReturnAccepted</c>, <c>ModelApproved</c>, <c>ModelDeclined</c>) by the reviewer who
    /// submitted the claim; a <c>SELF_REVIEW_REFUSED</c> security event was recorded (research R5).
    /// </summary>
    public sealed record SelfReviewRefused(string Detail) : RecordReplacementEventResult;

    /// <summary>412: <c>If-Match</c> is missing or does not equal the replacement's current ETag.</summary>
    public sealed record PreconditionFailed(string Detail) : RecordReplacementEventResult;

    /// <summary>
    /// 409: the event is not allowed from the replacement's current <paramref name="Status"/> (the allowed events for the
    /// caller's role are listed), the claim has no replacement, or another request changed the replacement concurrently.
    /// </summary>
    public sealed record Conflict(string Detail, ReplacementStatus? Status, IReadOnlyList<ReplacementEventType> AllowedEvents)
        : RecordReplacementEventResult;

    /// <summary>400 ValidationProblem: field (<c>type</c>, <c>reportedAt</c>, <c>serialNumber</c>, …) → messages.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : RecordReplacementEventResult;
}

/// <summary>
/// Records a staff update of a replacement (003 FR-009 – FR-028, research R4 – R7, R10, R12). In order: the caller must
/// be a claims agent or reviewer (403); the claim must be visible in the tenant (404) and have a replacement (409); a
/// reviewer-only event (<c>ReturnAccepted</c>, <c>ModelApproved</c>, <c>ModelDeclined</c>) needs a claims reviewer (403)
/// who did not submit the claim (403 and a <c>SELF_REVIEW_REFUSED</c> security event, 002 research R4); the request fields
/// must be valid for the event type and <c>reportedAt</c> neither in the future nor before the replacement was set up or
/// the latest entry it follows (400); <c>If-Match</c> must equal the replacement's row version (412); a location must be an
/// eligible replacement location of the tenant (<see cref="ServiceCenterPurpose.Replacement"/>, 404 + <c>ACCESS_DENIED</c>);
/// a proposed model must be a model of the tenant's catalog (404 + <c>ACCESS_DENIED</c>) that the replacement's product
/// rule allows (<see cref="ReplacementPolicy.CheckModel"/>, 400 naming the rule, checked before the status so that a
/// company allowing the same model only refuses by its rule, FR-023); the event must be allowed from the current status
/// for the caller's role (409 naming the status and the allowed events); a reserved unit's serial must follow FR-022 (400)
/// and a hand-over under <see cref="ReplacementReturnRule.AtCollection"/> needs the return confirmed (400, FR-021). One
/// transaction then writes the replacement, its <c>audit.replacement_events</c> row and a trail entry; <c>UnitReserved</c>
/// queues the <see cref="ReadyTemplate"/> notification with the dedupe key <c>replacement-ready:{unitId}</c>, and
/// <c>HandedToCustomer</c> completes the remedy and registers the handed-out serial in <c>catalog.replacement_serials</c>
/// (and <c>catalog.product_serials</c> when absent) with its cover end. A <c>ReturnRecorded</c> serial is compared with the
/// claim's (<see cref="EvidenceMatchRules.IdentifiersMatch"/>); a mismatch is recorded, queues no notification and blocks
/// the reservation and the hand-over until a reviewer records <c>ReturnAccepted</c>. <c>NotAvailable</c> evaluates the
/// remedy's policy version again with its <c>replacementNotAvailable</c> row (<see cref="RemedyPolicyEvaluator"/>, an
/// <c>Evaluated</c> remedy decision) and sends the remedy back to a reviewer (<c>AwaitingDecision</c>,
/// <c>REPLACEMENT_NOT_AVAILABLE</c>; FR-026, even for a single remedy). A concurrent change detected on save is a 409.
/// The events of later stories (refusals, moves, corrections) are not recorded yet: a 400 on <c>type</c>.
/// </summary>
public sealed class RecordReplacementEvent(
    ITenantContext tenant,
    IClaimRepository claims,
    ICatalogRepository catalog,
    IRemedyRepository remedies,
    IReplacementRepository replacements,
    IReplacementSerialRegistry serials,
    IPolicyRepository policies,
    IServiceNetwork network,
    RemedyPolicyEvaluator evaluator,
    ReplacementPolicy replacementPolicy,
    INotificationService notifications,
    IDecisionTrailWriter trail,
    ISecurityEventWriter securityEvents,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    /// <summary>Notification template queued when a replacement unit is reserved (research R7).</summary>
    public const string ReadyTemplate = "ReplacementReadyForCollection";

    public const int MinReasonLength = 10;

    public const int MaxReasonLength = 1000;

    public const int MaxNoteLength = 1000;

    public const int MaxSerialLength = 100;

    public const string NoReplacementDetail = "This claim has no replacement.";

    public const string MissingIfMatchDetail = "An If-Match header with the replacement's ETag is required.";

    public const string PreconditionDetail = "The replacement has changed since it was loaded; reload it and record the update again.";

    public const string ConcurrentChangeDetail = "The replacement was changed by another request; reload it and record the update again.";

    public const string ForbiddenDetail = "Only claims agents and claims reviewers record replacement updates.";

    public const string RemedyNotInProgressDetail =
        "The claim's remedy is not a replacement in progress, so the replacement cannot be handed to the customer.";

    public const string NotAvailableRemedyDetail =
        "The claim's remedy is not a replacement in progress, so it cannot be sent back to a reviewer as not available.";

    public const string ReviewerOnlyDetail = "Only claims reviewers accept a returned product or decide a model proposal.";

    public const string SelfReviewDetail = "You submitted this claim; another reviewer must record this decision.";

    /// <summary>The equivalent-model rule as named in a refusal.</summary>
    private const string EquivalentRule = $"rule {nameof(ReplacementProductRule.SameCategoryAtLeastClaimValue)}";

    /// <summary>The event types this use case records (User Stories 1 – 3); the others come with later stories.</summary>
    private static readonly HashSet<ReplacementEventType> Supported =
    [
        ReplacementEventType.LocationChanged,
        ReplacementEventType.ReturnRecorded,
        ReplacementEventType.ReturnAccepted,
        ReplacementEventType.ModelProposed,
        ReplacementEventType.ModelApproved,
        ReplacementEventType.ModelDeclined,
        ReplacementEventType.UnitReserved,
        ReplacementEventType.HandedToCustomer,
        ReplacementEventType.NotAvailable,
    ];

    /// <summary>The dedupe key of the ready notification of the reserved unit <paramref name="unitId"/> (research R7).</summary>
    public static string ReadyDedupeKey(Guid unitId) => $"replacement-ready:{unitId}";

    /// <summary>The staff role a replacement update is recorded with: reviewer before agent; null for any other principal.</summary>
    public static StaffRole? StaffRoleOf(IReadOnlySet<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return roles.Contains(Principals.ClaimsReviewerRole) ? StaffRole.ClaimsReviewer
            : roles.Contains(Principals.ClaimsAgentRole) ? StaffRole.ClaimsAgent
            : null;
    }

    public async Task<RecordReplacementEventResult> ExecuteAsync(RecordReplacementEventCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!tenant.IsResolved || tenant.IsSystem)
        {
            throw new InvalidOperationException("A replacement update requires a staff principal of a resolved tenant.");
        }

        if (StaffRoleOf(tenant.Roles) is not { } role)
        {
            return new RecordReplacementEventResult.Forbidden(ForbiddenDetail);
        }

        var claim = await claims.GetAsync(command.ClaimId, ct);
        if (claim is null || claim.TenantId != tenant.TenantId)
        {
            return new RecordReplacementEventResult.NotFound();
        }

        var replacement = await replacements.GetByClaimAsync(claim.Id, ct);
        if (replacement is null || replacement.TenantId != tenant.TenantId)
        {
            return new RecordReplacementEventResult.Conflict(NoReplacementDetail, null, []);
        }

        // Reviewer-only events (research R5): a claims agent is refused (403, not 409), and so is the reviewer who submitted
        // the claim, with a SELF_REVIEW_REFUSED security event (002 research R4).
        if (command.Type is { } requested && IsReviewerOnly(requested))
        {
            if (role != StaffRole.ClaimsReviewer)
            {
                return new RecordReplacementEventResult.Forbidden(ReviewerOnlyDetail);
            }

            if (claim.WasSubmittedBy(tenant.PrincipalId))
            {
                await securityEvents.RecordAsync(
                    SecurityEventKind.SelfReviewRefused, tenant.PrincipalName, claim.Id.ToString(),
                    new { reference = claim.Reference, eventType = requested.ToString() }, ct);
                return new RecordReplacementEventResult.SelfReviewRefused(SelfReviewDetail);
            }
        }

        var now = time.GetUtcNow();
        var errors = Validate(command, replacement, now);
        if (errors.Count > 0)
        {
            return new RecordReplacementEventResult.Invalid(errors);
        }

        if (!ClaimETag.TryParse(command.IfMatch, out var expected))
        {
            return new RecordReplacementEventResult.PreconditionFailed(MissingIfMatchDetail);
        }

        // A change between this read and the save is still caught: the save checks the row version the replacement was loaded with (409).
        if (await replacements.GetRowVersionAsync(claim.Id, ct) != expected)
        {
            return new RecordReplacementEventResult.PreconditionFailed(PreconditionDetail);
        }

        var type = command.Type!.Value;
        var product = claim.ProductId is { } claimedProductId ? await catalog.GetProductAsync(claimedProductId, ct) : null;
        if (type == ReplacementEventType.LocationChanged && !await IsEligibleLocationAsync(claim, product, command.LocationId!.Value, ct))
        {
            await securityEvents.RecordAsync(
                SecurityEventKind.AccessDenied, tenant.PrincipalName, command.LocationId.Value.ToString(),
                new { claimId = claim.Id, field = "locationId" }, ct);
            return new RecordReplacementEventResult.NotFound();
        }

        // A proposed model is checked against the replacement's product rule before the status: a company that allows the
        // same model only offers no proposal, and a proposal sent anyway is refused naming that rule (FR-023).
        if (type == ReplacementEventType.ModelProposed)
        {
            var proposed = await catalog.GetProductAsync(command.ProductId!.Value, ct);
            if (proposed is null || proposed.TenantId != tenant.TenantId)
            {
                await securityEvents.RecordAsync(
                    SecurityEventKind.AccessDenied, tenant.PrincipalName, command.ProductId.Value.ToString(),
                    new { claimId = claim.Id, field = "productId" }, ct);
                return new RecordReplacementEventResult.NotFound();
            }

            if (WhyNotProposable(replacement, product, proposed) is { } refusal)
            {
                return Invalid("productId", refusal);
            }
        }

        if (!replacement.AllowedEvents(role).Contains(type))
        {
            return NotAllowed(replacement, role, type);
        }

        // The serial seen is compared with the claim's after the 001 FR-016 normalisation; a mismatch is recorded too.
        var serialMatches = type == ReplacementEventType.ReturnRecorded
            && EvidenceMatchRules.IdentifiersMatch(claim.SerialNumber, command.SerialSeen) == EvidenceMatch.Match;

        Guid? unitProductId = null;
        if (type == ReplacementEventType.UnitReserved)
        {
            unitProductId = replacement.ApprovedProductId ?? claim.ProductId;
            var serialErrors = await CheckUnitSerialAsync(claim, unitProductId, command.SerialNumber!, ct);
            if (serialErrors is not null)
            {
                return new RecordReplacementEventResult.Invalid(serialErrors);
            }
        }

        var remedy = await remedies.GetAsync(claim.Id, ct);
        HandOver? handOver = null;
        if (type == ReplacementEventType.HandedToCustomer)
        {
            if (remedy is not { State: RemedyState.InProgress, Remedy: RemedyType.Replacement })
            {
                return new RecordReplacementEventResult.Conflict(RemedyNotInProgressDetail, replacement.Status, replacement.AllowedEvents(role));
            }

            if (replacement.ReturnRule == ReplacementReturnRule.AtCollection
                && replacement.ReturnState is not (ReturnState.Returned or ReturnState.HeldByRepairCentre or ReturnState.Accepted)
                && command.ReturnConfirmed != true)
            {
                return Invalid("returnConfirmed", "The return of the faulty product must be confirmed to hand the replacement to the customer.");
            }

            handOver = await HandOverBasisAsync(claim, replacement, command.HandedOverOn!.Value, ct);
        }

        RemedyBasis? basis = null;
        if (type == ReplacementEventType.NotAvailable)
        {
            if (remedy is not { State: RemedyState.InProgress, Remedy: RemedyType.Replacement })
            {
                return new RecordReplacementEventResult.Conflict(NotAvailableRemedyDetail, replacement.Status, replacement.AllowedEvents(role));
            }

            basis = await RemedyBasisAsync(remedy, ct);
        }

        var report = new ReplacementReport(
            command.ReportedAt!.Value, command.Source!.Value, tenant.PrincipalId, role, Trimmed(command.Note), now, tenant.PrincipalName);
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(
                async token =>
                {
                    var recorded = Apply(replacement, type, command, report, unitProductId, serialMatches);
                    replacements.AddEvent(recorded);
                    RemedyEvaluation? evaluation = null;
                    if (type == ReplacementEventType.NotAvailable)
                    {
                        // The replacement ended without a unit: the remedy goes back to a reviewer with the re-evaluation (FR-026).
                        evaluation = await ReEvaluateAsync(claim.Id, remedy!, basis!, now, token);
                    }

                    await AppendTrailAsync(claim.Id, replacement, recorded, Trimmed(command.Reason), evaluation, basis, token);

                    if (type == ReplacementEventType.UnitReserved && replacement.CurrentUnitId is { } unitId)
                    {
                        // A key already queued (a retried request) is tolerated: the database keeps one notification per unit.
                        await notifications.EnqueueAsync(claim.Id, ReadyTemplate, ReadyDedupeKey(unitId), token);
                    }

                    if (type == ReplacementEventType.HandedToCustomer)
                    {
                        await CompleteAsync(claim, replacement, remedy!, recorded, handOver!, now, token);
                    }
                },
                ct);
        }
        catch (ConcurrencyConflictException)
        {
            return new RecordReplacementEventResult.Conflict(ConcurrentChangeDetail, null, []);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new RecordReplacementEventResult.Forbidden(ex.Message);
        }
        catch (InvalidClaimTransitionException)
        {
            return NotAllowed(replacement, role, type);
        }
        catch (ArgumentException ex)
        {
            // The replacement refuses the data before changing anything.
            var field = ex.ParamName switch
            {
                null => "type",
                "serviceCenterId" => "locationId",
                var name => name,
            };
            return Invalid(field, ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty, StringComparison.Ordinal));
        }

        return new RecordReplacementEventResult.Recorded(replacement);
    }

    private static ReplacementEvent Apply(
        Replacement replacement,
        ReplacementEventType type,
        RecordReplacementEventCommand command,
        ReplacementReport report,
        Guid? unitProductId,
        bool serialMatches)
        => type switch
        {
            ReplacementEventType.LocationChanged => replacement.ChangeLocation(command.LocationId!.Value, command.Reason!.Trim(), report),
            ReplacementEventType.ReturnRecorded => replacement.RecordReturn(
                command.ReturnedOn!.Value, command.SerialSeen!.Trim(), serialMatches, report),
            ReplacementEventType.ReturnAccepted => replacement.AcceptReturn(command.Reason!.Trim(), report),
            ReplacementEventType.ModelProposed => replacement.ProposeModel(command.ProductId!.Value, command.Reason!.Trim(), report),
            ReplacementEventType.ModelApproved => replacement.ApproveModel(report),
            ReplacementEventType.ModelDeclined => replacement.DeclineModel(command.Reason!.Trim(), report),
            ReplacementEventType.NotAvailable => replacement.MarkNotAvailable(command.Reason!.Trim(), report),
            ReplacementEventType.UnitReserved => replacement.ReserveUnit(unitProductId!.Value, command.SerialNumber!, report),
            ReplacementEventType.HandedToCustomer => replacement.HandToCustomer(
                command.HandedOverOn!.Value, command.ReturnConfirmed == true, report),
            _ => throw new InvalidClaimTransitionException($"Cannot record {type} on a replacement in status {replacement.Status}."),
        };

    /// <summary>
    /// The hand-over completes the remedy (research R12) and registers the handed-out serial with its cover end in
    /// <c>catalog.replacement_serials</c>, adding it to <c>catalog.product_serials</c> when it is not there yet (research R10).
    /// </summary>
    private async Task CompleteAsync(
        Claim claim, Replacement replacement, ClaimRemedy remedy, ReplacementEvent recorded, HandOver handOver, DateTimeOffset now,
        CancellationToken ct)
    {
        var unit = replacement.CurrentUnit!;
        var registration = RegisteredReplacementSerial.Register(
            tenant.TenantId, unit.SerialNumber, unit.ProductId, claim.SerialNumber, claim.Id, handOver.OriginalPurchaseDate,
            handOver.HandedOverOn, handOver.CoverEndDate, now);
        serials.Register(registration);
        var addedToCatalog = handOver.CatalogSerial is null;
        if (addedToCatalog)
        {
            catalog.AddSerial(ProductSerial.Create(tenant.TenantId, unit.SerialNumber, unit.ProductId));
        }

        await trail.AppendAsync(
            claim.Id, TrailStep.ReplacementUnitRegistered, tenant.PrincipalId, "Replacement unit registered for later claims.",
            new
            {
                replacementId = replacement.Id,
                eventSeq = recorded.Seq,
                unitId = unit.Id,
                productId = unit.ProductId,
                originalPurchaseDate = handOver.OriginalPurchaseDate,
                handedOverOn = handOver.HandedOverOn,
                originalCoverageEndDate = handOver.OriginalCoverageEnd,
                minimumCoverMonths = replacement.MinimumCoverMonths,
                coverEndDate = handOver.CoverEndDate,
                policyVersionId = replacement.PolicyVersionId,
                addedToCatalog,
            },
            ct);

        remedy.Complete(RemedyType.Replacement, now);
        await trail.AppendAsync(
            claim.Id, TrailStep.RemedyCompleted, tenant.PrincipalId, "Replacement remedy completed: the unit was handed to the customer.",
            new { remedy = nameof(RemedyType.Replacement), replacementEventSeq = recorded.Seq, recordedAt = now }, ct);
    }

    /// <summary>
    /// Writes the <see cref="RemedyDecisionKind.Evaluated"/> row of the <c>replacementNotAvailable</c> row of the remedy's
    /// policy version and returns the remedy to a reviewer with its allowed remedies and pending reason: a single remedy is
    /// the recommendation, never set automatically (FR-026). The failed repair attempts carry over from the latest decision.
    /// </summary>
    private async Task<RemedyEvaluation> ReEvaluateAsync(
        Guid claimId, ClaimRemedy remedy, RemedyBasis basis, DateTimeOffset now, CancellationToken ct)
    {
        var decisions = await remedies.GetDecisionsAsync(claimId, ct);
        var latest = decisions.MaxBy(d => d.Seq);
        var seq = latest is null ? 1 : latest.Seq + 1;
        var failedAttempts = latest?.FailedAttempts ?? 0;
        var evaluation = evaluator.Evaluate(basis.Rules, RemedyScenario.ReplacementNotAvailable, failedAttempts);
        remedies.AddDecision(RemedyDecision.Evaluated(
            Guid.CreateVersion7(now), tenant.TenantId, claimId, seq, evaluation.Scenario, basis.PolicyVersionId, evaluation.TableRow,
            evaluation.AllowedRemedies, failedAttempts, estimateAmount: null, now));
        remedy.ReturnToDecision(
            basis.PolicyVersionId, evaluation.Scenario, evaluation.AllowedRemedies,
            evaluation.PendingReason ?? RemedyPendingReason.ReplacementNotAvailable, now);
        return evaluation;
    }

    /// <summary>The remedy's policy version (its rules are evaluated again) and its policy code and version for the trail.</summary>
    private async Task<RemedyBasis> RemedyBasisAsync(ClaimRemedy remedy, CancellationToken ct)
    {
        var version = await policies.GetVersionAsync(remedy.PolicyVersionId, ct);
        var policy = version is null ? null : await policies.GetPolicyAsync(version.PolicyId, ct);
        return new RemedyBasis(remedy.PolicyVersionId, version?.RemedyRules, policy?.Code, version?.Version);
    }

    /// <summary>
    /// Why <paramref name="proposed"/> may not be proposed instead of the claimed model, naming the replacement's product
    /// rule (<see cref="ReplacementPolicy.CheckModel"/> with the rules copied onto the replacement, FR-023); null when a
    /// reviewer may approve it.
    /// </summary>
    private string? WhyNotProposable(Replacement replacement, Product? claimed, Product proposed)
    {
        if (claimed is null)
        {
            return "The claimed product is not in the catalog, so no other model can be compared with it.";
        }

        var check = replacementPolicy.CheckModel(
            ReplacementModelQuery.RulesOf(replacement), ReplacementModelQuery.ModelOf(claimed), ReplacementModelQuery.ModelOf(proposed));
        return (check.Verdict, check.Refusal) switch
        {
            (ReplacementModelVerdict.NeedsReviewerApproval, _) => null,
            (ReplacementModelVerdict.SameModel, _)
                => $"{proposed.ModelCode} is the claimed model; it needs no proposal and a unit of it can be reserved.",
            (_, ReplacementModelRefusal.SameModelOnly)
                => $"The policy allows the same model only (rule {nameof(ReplacementProductRule.SameModel)}): "
                    + $"{claimed.ModelCode} cannot be replaced by {proposed.ModelCode}.",
            (_, ReplacementModelRefusal.OtherCategory)
                => $"The policy allows another model of the same category only ({EquivalentRule}): "
                    + $"{proposed.ModelCode} is in category {proposed.Category}, the claimed {claimed.ModelCode} in {claimed.Category}.",
            (_, ReplacementModelRefusal.LowerClaimValue)
                => $"The policy allows another model only with a claim value of at least the claimed model's ({EquivalentRule}): "
                    + $"{proposed.ModelCode} has {proposed.ClaimValue}, {claimed.ModelCode} {claimed.ClaimValue}.",
            _ => $"The claim values of {proposed.ModelCode} and {claimed.ModelCode} cannot be compared ({EquivalentRule}).",
        };
    }

    /// <summary>
    /// What a hand-over registers: the original purchase date (carried forward when the claim's serial is itself a
    /// handed-out replacement), the original coverage end from the standard months of the claim's region in the
    /// replacement's policy version (the hand-over date when it cannot be determined), the cover end
    /// (<see cref="RegisteredReplacementSerial.CoverEndOf"/>) and the catalog entry of the unit's serial, if any.
    /// </summary>
    private async Task<HandOver> HandOverBasisAsync(Claim claim, Replacement replacement, DateOnly handedOverOn, CancellationToken ct)
    {
        var chained = await serials.FindBySerialAsync(claim.SerialNumber, ct);
        var purchaseDate = chained?.OriginalPurchaseDate ?? claim.PurchaseDate;
        var version = await policies.GetVersionAsync(replacement.PolicyVersionId, ct);
        var window = claim.Region is { } region
            ? CoverageWindowCalculator.Calculate(version?.Terms, region, component: null, purchaseDate, handedOverOn)
            : null;
        var originalCoverageEnd = window?.CoverageEndDate ?? handedOverOn;
        var coverEnd = RegisteredReplacementSerial.CoverEndOf(originalCoverageEnd, handedOverOn, replacement.MinimumCoverMonths);
        var catalogSerial = await catalog.FindSerialAsync(replacement.CurrentUnit!.SerialNumber, ct);
        return new HandOver(handedOverOn, purchaseDate, originalCoverageEnd, coverEnd, catalogSerial);
    }

    /// <summary>
    /// The serial rules of a reserved unit (FR-022): not the claim's serial, not handed out before or the serial of another
    /// claim, and, when in the catalog, registered to the reserved model. Null when the serial may be reserved.
    /// </summary>
    private async Task<Dictionary<string, string[]>?> CheckUnitSerialAsync(
        Claim claim, Guid? productId, string serialNumber, CancellationToken ct)
    {
        if (productId is null)
        {
            return Errors("serialNumber", "The claimed product is not in the catalog, so no replacement unit can be reserved for it.");
        }

        var serial = ProductSerial.NormalizeSerial(serialNumber);
        if (serial == ProductSerial.NormalizeSerial(claim.SerialNumber))
        {
            return Errors("serialNumber", "The replacement unit's serial must differ from the claim's serial.");
        }

        if (await serials.IsSerialInUseAsync(serial, ct))
        {
            return Errors("serialNumber", "This serial was already handed out as a replacement or belongs to a claim.");
        }

        if (await catalog.FindSerialAsync(serial, ct) is { } registered && registered.ProductId != productId)
        {
            return Errors("serialNumber", "This serial is registered to another model in the catalog.");
        }

        return null;
    }

    private Task AppendTrailAsync(
        Guid claimId,
        Replacement replacement,
        ReplacementEvent recorded,
        string? reason,
        RemedyEvaluation? evaluation,
        RemedyBasis? basis,
        CancellationToken ct)
    {
        var (step, summary) = recorded.Type switch
        {
            ReplacementEventType.ModelProposed
                => (TrailStep.ReplacementModelProposed, "Another model proposed as the replacement; a reviewer must approve it."),
            ReplacementEventType.ModelApproved
                => (TrailStep.ReplacementModelDecided, "Proposed replacement model approved by a reviewer."),
            ReplacementEventType.ModelDeclined
                => (TrailStep.ReplacementModelDecided, "Proposed replacement model declined by a reviewer; the claimed model remains."),
            ReplacementEventType.NotAvailable
                => (TrailStep.ReplacementStatusUpdated, "No replacement available; the policy was evaluated again and the remedy goes to a reviewer."),
            ReplacementEventType.LocationChanged => (TrailStep.ReplacementLocationAssigned, "Replacement location changed by staff."),
            ReplacementEventType.ReturnRecorded when replacement.ReturnState == ReturnState.Mismatch
                => (TrailStep.FaultyProductReturned, "Faulty product returned to the replacement location; its serial does not match the claim."),
            ReplacementEventType.ReturnRecorded => (TrailStep.FaultyProductReturned, "Faulty product returned to the replacement location."),
            ReplacementEventType.ReturnAccepted
                => (TrailStep.ReplacementStatusUpdated, "Returned product accepted by a reviewer despite the serial mismatch."),
            ReplacementEventType.UnitReserved => (TrailStep.ReplacementStatusUpdated, "Replacement unit reserved; ready for collection."),
            ReplacementEventType.HandedToCustomer => (TrailStep.ReplacementStatusUpdated, "Replacement unit handed to the customer."),
            var other => (TrailStep.ReplacementStatusUpdated, $"Replacement update recorded: {other}."),
        };

        var location = replacement.ActiveLocation;
        var unit = replacement.CurrentUnit;
        var payload = new Dictionary<string, object?>
        {
            ["replacementId"] = replacement.Id,
            ["eventSeq"] = recorded.Seq,
            ["type"] = recorded.Type.ToString(),
            ["fromStatus"] = recorded.FromStatus?.ToString(),
            ["toStatus"] = recorded.ToStatus?.ToString(),
            ["reportedAt"] = recorded.ReportedAt,
            ["recordedAt"] = recorded.RecordedAt,
            ["source"] = recorded.Source.ToString(),
            ["serviceCenterId"] = location?.ServiceCenterId,
            ["returnState"] = replacement.ReturnState.ToString(),
        };

        switch (recorded.Type)
        {
            case ReplacementEventType.LocationChanged:
                payload["assignment"] = location?.Assignment.ToString();
                payload["reason"] = location?.Reason;
                break;
            case ReplacementEventType.ReturnRecorded:
                payload["returnedOn"] = replacement.Return?.ReturnedOn;
                payload["serialMatches"] = replacement.Return?.SerialMatches;
                break;
            case ReplacementEventType.ReturnAccepted:
                payload["reason"] = reason;
                break;
            case ReplacementEventType.ModelProposed:
            case ReplacementEventType.ModelApproved:
            case ReplacementEventType.ModelDeclined:
                var proposalId = ProposalIdOf(recorded);
                var proposal = replacement.Proposals.SingleOrDefault(p => p.Id == proposalId);
                payload["proposalId"] = proposal?.Id;
                payload["productId"] = proposal?.ProductId;
                payload["proposalState"] = proposal?.State.ToString();
                payload["approvedProductId"] = replacement.ApprovedProductId;
                payload["reason"] = reason;
                break;
            case ReplacementEventType.NotAvailable:
                payload["reason"] = reason;
                break;
            case ReplacementEventType.UnitReserved:
            case ReplacementEventType.HandedToCustomer:
                payload["unitId"] = unit?.Id;
                payload["unitSeq"] = unit?.Seq;
                payload["productId"] = unit?.ProductId;
                payload["unitResult"] = unit?.Result.ToString();
                break;
        }

        if (evaluation is not null && basis is not null)
        {
            payload["policyCode"] = basis.PolicyCode;
            payload["policyVersion"] = basis.Version;
            payload["policyVersionId"] = basis.PolicyVersionId;
            payload["scenario"] = evaluation.Scenario.ToString();
            payload["tableRow"] = evaluation.TableRow;
            payload["allowedRemedies"] = evaluation.AllowedRemedies.Select(r => r.ToString()).ToArray();
            payload["recommendedRemedy"] = evaluation.SingleRemedy?.ToString();
            payload["pendingReason"] = (evaluation.PendingReason ?? RemedyPendingReason.ReplacementNotAvailable).ToString();
        }

        return trail.AppendAsync(claimId, step, tenant.PrincipalId, summary, payload, ct);
    }

    /// <summary>The proposal a model event refers to (its payload's <c>proposalId</c>).</summary>
    private static Guid? ProposalIdOf(ReplacementEvent modelEvent)
    {
        using var payload = JsonDocument.Parse(modelEvent.PayloadJson);
        return payload.RootElement.ValueKind == JsonValueKind.Object
            && payload.RootElement.TryGetProperty("proposalId", out var id)
            && id.TryGetGuid(out var proposalId)
                ? proposalId
                : null;
    }

    private async Task<bool> IsEligibleLocationAsync(Claim claim, Product? product, Guid locationId, CancellationToken ct)
    {
        var locations = await RepairCentreEligibility.ListAsync(network, claim, product, ServiceCenterPurpose.Replacement, ct);
        return locations.Any(l => l.Id == locationId);
    }

    /// <summary>Events only a claims reviewer who did not submit the claim may record (research R5, FR-024).</summary>
    private static bool IsReviewerOnly(ReplacementEventType type)
        => type is ReplacementEventType.ReturnAccepted or ReplacementEventType.ModelApproved or ReplacementEventType.ModelDeclined;

    private static RecordReplacementEventResult.Conflict NotAllowed(Replacement replacement, StaffRole role, ReplacementEventType type)
    {
        var allowed = replacement.AllowedEvents(role);
        return new RecordReplacementEventResult.Conflict(
            $"{type} is not allowed while the replacement is {replacement.Status}; allowed events: "
            + (allowed.Count == 0 ? "none" : string.Join(", ", allowed)) + ".",
            replacement.Status,
            allowed);
    }

    /// <summary>Field errors of the request for its event type (contracts <c>ReplacementEventRequest</c>, FR-015).</summary>
    private static Dictionary<string, string[]> Validate(RecordReplacementEventCommand command, Replacement replacement, DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Add(string field, string message) => errors[field] = [message];

        if (command.Type is not { } type)
        {
            Add("type", "The event type is required.");
        }
        else if (!Supported.Contains(type))
        {
            Add("type", Enum.IsDefined(type)
                ? $"{type} cannot be recorded through this request yet."
                : "The event type is not a replacement event type.");
        }

        if (command.Source is null)
        {
            Add("source", "The source of the report is required.");
        }
        else if (command.Source == ReportSource.System || !Enum.IsDefined(command.Source.Value))
        {
            Add("source", "The source must be Phone, Email, InPerson or Other.");
        }

        if (command.Note is { } note && note.Trim().Length > MaxNoteLength)
        {
            Add("note", $"A note has at most {MaxNoteLength} characters.");
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        switch (command.Type)
        {
            case ReplacementEventType.LocationChanged:
                if (command.LocationId is null || command.LocationId == Guid.Empty)
                {
                    Add("locationId", "The replacement location is required.");
                }

                ValidateReason(command.Reason, Add);
                break;

            case ReplacementEventType.ReturnRecorded:
                if (command.ReturnedOn is not { } returnedOn)
                {
                    Add("returnedOn", "The return date is required.");
                }
                else if (returnedOn > today)
                {
                    Add("returnedOn", "The return date must not be in the future.");
                }

                ValidateSerial("serialSeen", command.SerialSeen, "The serial seen on the returned product", Add);
                break;

            case ReplacementEventType.ReturnAccepted:
            case ReplacementEventType.ModelDeclined:
            case ReplacementEventType.NotAvailable:
                ValidateReason(command.Reason, Add);
                break;

            case ReplacementEventType.ModelProposed:
                if (command.ProductId is null || command.ProductId == Guid.Empty)
                {
                    Add("productId", "The proposed model is required.");
                }

                ValidateReason(command.Reason, Add);
                break;

            case ReplacementEventType.UnitReserved:
                ValidateSerial("serialNumber", command.SerialNumber, "The serial number of the reserved unit", Add);
                break;

            case ReplacementEventType.HandedToCustomer:
                if (command.HandedOverOn is not { } handedOverOn)
                {
                    Add("handedOverOn", "The hand-over date is required.");
                }
                else if (handedOverOn > today)
                {
                    Add("handedOverOn", "The hand-over date must not be in the future.");
                }
                else if (handedOverOn < DateOnly.FromDateTime(replacement.CreatedAt.UtcDateTime))
                {
                    Add("handedOverOn", $"The hand-over date must not be before the replacement was set up on {replacement.CreatedAt:yyyy-MM-dd}.");
                }

                break;
        }

        DateTimeOffset? latest = replacement.Events.Count == 0 ? null : replacement.Events.Max(e => e.ReportedAt);
        if (command.ReportedAt is not { } reportedAt)
        {
            Add("reportedAt", "The time the location reported the update is required.");
        }
        else if (reportedAt > now)
        {
            Add("reportedAt", "The reported time must not be in the future.");
        }
        else if (reportedAt < replacement.CreatedAt)
        {
            Add("reportedAt", "The reported time must not be before the replacement was set up.");
        }
        else if (latest is { } last && reportedAt < last)
        {
            Add("reportedAt", $"The reported time must not be before the latest replacement entry ({last.UtcDateTime:yyyy-MM-dd HH:mm} UTC).");
        }

        return errors;
    }

    private static void ValidateReason(string? reason, Action<string, string> add)
    {
        var length = reason?.Trim().Length ?? 0;
        if (length == 0)
        {
            add("reason", "A reason is required.");
        }
        else if (length < MinReasonLength || length > MaxReasonLength)
        {
            add("reason", $"A reason has {MinReasonLength} to {MaxReasonLength} characters.");
        }
    }

    private static void ValidateSerial(string field, string? serial, string what, Action<string, string> add)
    {
        var length = serial?.Trim().Length ?? 0;
        if (length == 0)
        {
            add(field, $"{what} is required.");
        }
        else if (length > MaxSerialLength)
        {
            add(field, $"A serial number has at most {MaxSerialLength} characters.");
        }
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Dictionary<string, string[]> Errors(string field, string message)
        => new(StringComparer.Ordinal) { [field] = [message] };

    private static RecordReplacementEventResult.Invalid Invalid(string field, string message) => new(Errors(field, message));

    /// <summary>What a hand-over registers (research R10) and the catalog entry of the handed-out serial, if any.</summary>
    private sealed record HandOver(
        DateOnly HandedOverOn, DateOnly OriginalPurchaseDate, DateOnly OriginalCoverageEnd, DateOnly CoverEndDate, ProductSerial? CatalogSerial);

    /// <summary>The remedy's policy version and rules (null when the version has none) evaluated after <c>NotAvailable</c>.</summary>
    private sealed record RemedyBasis(Guid PolicyVersionId, RemedyRules? Rules, string? PolicyCode, int? Version);
}
