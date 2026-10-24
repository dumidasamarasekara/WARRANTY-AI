using System.Text.Json;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Claims;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Remedies;

namespace Warranty.Application.Fulfillment;

/// <summary>
/// A repair-centre update reported to staff (002 contracts, <c>RepairEventRequest</c>) with the <c>If-Match</c> value
/// (the repair ETag) it was sent with. Fields are nullable so that a missing one is a 400 naming it; which fields an
/// event needs is listed on <c>RepairEventRequest</c>. The staff member is the principal of <see cref="ITenantContext"/>,
/// never a request field.
/// </summary>
public sealed record RecordRepairEventCommand(
    Guid ClaimId,
    string? IfMatch,
    RepairEventType? Type,
    DateTimeOffset? ReportedAt,
    ReportSource? Source,
    string? Note = null,
    Guid? RepairCentreId = null,
    string? Reason = null,
    DateOnly? HandedOverOn = null,
    string? CentreReference = null,
    decimal? EstimateAmount = null,
    ProductLocation? ProductLocation = null,
    int? CorrectsSeq = null,
    RepairStatus? ToStatus = null);

/// <summary>Outcome of <see cref="RecordRepairEvent.ExecuteAsync"/> (plan.md error mapping).</summary>
public abstract record RecordRepairEventResult
{
    private RecordRepairEventResult()
    {
    }

    /// <summary>201: the event was recorded; the updated repair is returned.</summary>
    public sealed record Recorded(Repair Repair) : RecordRepairEventResult;

    /// <summary>404: no such claim, or a repair centre that is not an eligible centre of the tenant (<c>ACCESS_DENIED</c>).</summary>
    public sealed record NotFound : RecordRepairEventResult;

    /// <summary>412: <c>If-Match</c> is missing or does not equal the repair's current ETag.</summary>
    public sealed record PreconditionFailed(string Detail) : RecordRepairEventResult;

    /// <summary>
    /// 409: the event is not allowed from the repair's current <paramref name="Status"/> (the allowed events are
    /// listed), the claim has no repair, or another request changed the repair concurrently.
    /// </summary>
    public sealed record Conflict(string Detail, RepairStatus? Status, IReadOnlyList<RepairEventType> AllowedEvents)
        : RecordRepairEventResult;

    /// <summary>400 ValidationProblem: field (<c>type</c>, <c>reportedAt</c>, <c>source</c>, …) → messages.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : RecordRepairEventResult;
}

/// <summary>
/// Records a staff update of a repair (002 FR-019 – FR-032, FR-038, research R2, R5 – R7). In order: the claim must be
/// visible in the tenant (404) and have a repair (409); the request fields must be valid for the event type and
/// <c>reportedAt</c> neither in the future nor before the hand-over (400); <c>If-Match</c> must equal the repair's row
/// version (412); a repair centre must be an eligible centre of the tenant (404 + <c>ACCESS_DENIED</c>); the event must
/// be allowed from the current status (409 naming the status and the allowed events). One transaction then writes the
/// repair, its <c>audit.repair_events</c> row and a trail entry; <c>MarkedReady</c> queues the
/// <c>RepairReadyForCollection</c> notification with the dedupe key <c>repair-ready:{attemptId}</c>, and
/// <c>Collected</c> completes the remedy. A failure or an uneconomical estimate ends the attempt (trail
/// <c>RepairAttemptEnded</c>) and re-evaluates the remedy with the claim's own policy version
/// (<see cref="RemedyPolicyEvaluator"/>, an <c>Evaluated</c> remedy decision): below the attempt limit a new attempt is
/// allowed, otherwise the remedy goes back to a reviewer (<c>REPAIR_FAILED_LIMIT</c>, <c>REPAIR_NOT_ECONOMICAL</c>) and
/// no new attempt can start. A transfer moves the repair to another eligible centre of the tenant (404 + <c>ACCESS_DENIED</c>
/// otherwise) and a correction applies one corrected value while the corrected entry stays unchanged (FR-026, FR-027); a
/// corrected ready mark reopens the same attempt, so its ready notification is never queued twice. A concurrent change
/// detected on save is a 409. The roles (claims-agent, claims-reviewer) are enforced by the endpoint.
/// </summary>
public sealed class RecordRepairEvent(
    ITenantContext tenant,
    IClaimRepository claims,
    ICatalogRepository catalog,
    IRemedyRepository remedies,
    IRepairRepository repairs,
    IPolicyRepository policies,
    IServiceNetwork network,
    RemedyPolicyEvaluator evaluator,
    INotificationService notifications,
    IDecisionTrailWriter trail,
    ISecurityEventWriter securityEvents,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    /// <summary>Notification template queued when a repair becomes ready for collection.</summary>
    public const string ReadyTemplate = "RepairReadyForCollection";

    public const int MinReasonLength = 10;

    public const int MaxReasonLength = 1000;

    /// <summary>The largest estimate a <c>numeric(12,2)</c> column holds.</summary>
    public const decimal MaxEstimateAmount = 9_999_999_999.99m;

    public const string NoRepairDetail = "This claim has no repair.";

    public const string MissingIfMatchDetail = "An If-Match header with the repair's ETag is required.";

    public const string PreconditionDetail = "The repair has changed since it was loaded; reload it and record the update again.";

    public const string ConcurrentChangeDetail = "The repair was changed by another request; reload it and record the update again.";

    public const string SentBackAfterReEvaluationDetail =
        "The remedy already waits for a reviewer's decision after this entry, so the entry cannot be sent back.";

    public const string RemedyNotInProgressDetail =
        "The claim's remedy is not a repair in progress, so the repair cannot be updated this way.";

    /// <summary>The dedupe key of the ready notification of <paramref name="attemptId"/> (research R7).</summary>
    public static string ReadyDedupeKey(Guid attemptId) => $"repair-ready:{attemptId}";

    /// <summary>
    /// The events allowed for <paramref name="repair"/> given the claim's <paramref name="remedy"/>: the repair's own list,
    /// without <see cref="RepairEventType.NewAttemptStarted"/> once the remedy has left the repair (attempt limit reached,
    /// FR-031), and none once a reviewer decided another remedy: the repair is closed (T249). Drives
    /// <c>RepairView.allowedEvents</c> and the 409 detail.
    /// </summary>
    public static IReadOnlyList<RepairEventType> AllowedEvents(Repair repair, ClaimRemedy? remedy)
    {
        ArgumentNullException.ThrowIfNull(repair);
        if (IsClosedBy(remedy))
        {
            return [];
        }

        var allowed = repair.AllowedEvents();
        return IsRepairInProgress(remedy) ? allowed : [.. allowed.Where(e => e != RepairEventType.NewAttemptStarted)];
    }

    /// <summary>A repair is closed once its claim's remedy was decided as Replacement or Refund (002 FR-031, FR-032, T249).</summary>
    public static bool IsClosedBy(ClaimRemedy? remedy)
        => remedy is { State: not RemedyState.AwaitingDecision, Remedy: { } decided } && decided != RemedyType.Repair;

    public async Task<RecordRepairEventResult> ExecuteAsync(RecordRepairEventCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!tenant.IsResolved || tenant.IsSystem)
        {
            throw new InvalidOperationException("A repair update requires a staff principal of a resolved tenant.");
        }

        var claim = await claims.GetAsync(command.ClaimId, ct);
        if (claim is null || claim.TenantId != tenant.TenantId)
        {
            return new RecordRepairEventResult.NotFound();
        }

        var repair = await repairs.GetByClaimAsync(claim.Id, ct);
        if (repair is null || repair.TenantId != tenant.TenantId)
        {
            return new RecordRepairEventResult.Conflict(NoRepairDetail, null, []);
        }

        var now = time.GetUtcNow();
        var errors = Validate(command, repair, now);
        if (errors.Count > 0)
        {
            return new RecordRepairEventResult.Invalid(errors);
        }

        if (!ClaimETag.TryParse(command.IfMatch, out var expected))
        {
            return new RecordRepairEventResult.PreconditionFailed(MissingIfMatchDetail);
        }

        // A change between this read and the save is still caught: the save checks the row version the repair was loaded with (409).
        if (await repairs.GetRowVersionAsync(claim.Id, ct) != expected)
        {
            return new RecordRepairEventResult.PreconditionFailed(PreconditionDetail);
        }

        var type = command.Type!.Value;
        if (type is RepairEventType.CentreChanged or RepairEventType.Transferred
            && !await IsEligibleCentreAsync(claim, command.RepairCentreId!.Value, ct))
        {
            await securityEvents.RecordAsync(
                SecurityEventKind.AccessDenied, tenant.PrincipalName, command.RepairCentreId.Value.ToString(),
                new { claimId = claim.Id, field = "repairCentreId" }, ct);
            return new RecordRepairEventResult.NotFound();
        }

        var remedy = await remedies.GetAsync(claim.Id, ct);
        if (!AllowedEvents(repair, remedy).Contains(type))
        {
            return NotAllowed(repair, remedy, type);
        }

        if (type is RepairEventType.Collected or RepairEventType.EstimateReported or RepairEventType.RepairFailed
                or RepairEventType.NewAttemptStarted
            && !IsRepairInProgress(remedy))
        {
            return new RecordRepairEventResult.Conflict(RemedyNotInProgressDetail, repair.Status, AllowedEvents(repair, remedy));
        }

        // Sending a failure or an uneconomical estimate back would need the remedy still to be the repair in progress.
        if (type == RepairEventType.Correction
            && command.ToStatus is not null
            && repair.Events.SingleOrDefault(e => e.Seq == command.CorrectsSeq) is { Type: RepairEventType.RepairFailed or RepairEventType.EstimateReported }
            && !IsRepairInProgress(remedy))
        {
            return new RecordRepairEventResult.Conflict(SentBackAfterReEvaluationDetail, repair.Status, AllowedEvents(repair, remedy));
        }

        var basis = type is RepairEventType.EstimateReported or RepairEventType.RepairFailed
            ? await RemedyBasisAsync(claim, remedy!, ct)
            : null;

        var report = new RepairReport(
            command.ReportedAt!.Value, command.Source!.Value, tenant.PrincipalId, command.Note, now, tenant.PrincipalName);
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(
                async token =>
                {
                    var (recorded, evaluation) = Apply(repair, type, command, report, basis);
                    repairs.AddEvent(recorded);

                    if (evaluation is not null)
                    {
                        // The attempt ended: record the re-evaluation and, at the limit or above the economic limit, return the remedy to a reviewer.
                        await ReEvaluateAsync(claim.Id, remedy!, repair, evaluation, basis!, now, token);
                    }

                    await AppendTrailAsync(claim.Id, repair, recorded, evaluation, basis, token);

                    if (type == RepairEventType.MarkedReady && repair.CurrentAttemptId is { } attemptId)
                    {
                        await notifications.EnqueueAsync(claim.Id, ReadyTemplate, ReadyDedupeKey(attemptId), token);
                    }

                    if (type == RepairEventType.Collected)
                    {
                        remedy!.Complete(RemedyType.Repair, now);
                        await trail.AppendAsync(
                            claim.Id, TrailStep.RemedyCompleted, tenant.PrincipalId, "Repair remedy completed: the product was collected.",
                            new { remedy = nameof(RemedyType.Repair), repairEventSeq = recorded.Seq, recordedAt = now }, token);
                    }
                },
                ct);
        }
        catch (ConcurrencyConflictException)
        {
            return new RecordRepairEventResult.Conflict(ConcurrentChangeDetail, null, []);
        }
        catch (InvalidClaimTransitionException)
        {
            return NotAllowed(repair, remedy, type);
        }
        catch (ArgumentException ex) when (type is RepairEventType.Transferred or RepairEventType.Correction)
        {
            // The repair refuses the transfer or correction before changing anything (another centre, an entry that fits the value).
            var field = ex.ParamName switch
            {
                null => "correctsSeq",
                "serviceCenterId" => "repairCentreId",
                var name => name,
            };
            return new RecordRepairEventResult.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [field] = [ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty, StringComparison.Ordinal)],
            });
        }

        return new RecordRepairEventResult.Recorded(repair);
    }

    /// <summary>
    /// Applies the event to the repair. For an estimate the economic limit comes from the evaluator; when the estimate or a
    /// failure ends the attempt, the evaluation of the claim's own rules is returned for the re-evaluation.
    /// </summary>
    private (RepairEvent Recorded, RemedyEvaluation? Evaluation) Apply(
        Repair repair, RepairEventType type, RecordRepairEventCommand command, RepairReport report, RemedyBasis? basis)
    {
        switch (type)
        {
            case RepairEventType.CentreChanged:
                return (repair.ChangeCentre(command.RepairCentreId!.Value, command.Reason!.Trim(), report), null);
            case RepairEventType.HandedOver:
                return (repair.RecordHandover(command.HandedOverOn!.Value, command.CentreReference, report), null);
            case RepairEventType.MarkedReady:
                return (repair.MarkReady(report), null);
            case RepairEventType.Collected:
                return (repair.RecordCollection(report), null);
            case RepairEventType.NewAttemptStarted:
                return (repair.StartNewAttempt(command.ProductLocation!.Value, report), null);
            case RepairEventType.Transferred:
                return (repair.Transfer(command.RepairCentreId!.Value, command.Reason!.Trim(), report), null);
            case RepairEventType.Correction:
                var correction = new RepairCorrection(command.HandedOverOn, command.CentreReference, command.EstimateAmount, command.ToStatus);
                return (repair.Correct(command.CorrectsSeq!.Value, command.Reason!.Trim(), correction, report), null);
            case RepairEventType.EstimateReported:
            {
                var estimate = command.EstimateAmount!.Value;
                var evaluation = evaluator.Evaluate(
                    basis!.Rules, RemedyScenario.RepairNotEconomical, repair.FailedAttempts, estimate, basis.ClaimValue);
                var recorded = repair.RecordEstimate(estimate, evaluation.EconomicLimitAmount, report);
                return (recorded, repair.NotPossibleReason == RepairNotPossibleReason.NotEconomical ? evaluation : null);
            }

            case RepairEventType.RepairFailed:
            {
                var recorded = repair.RecordFailure(command.Reason!.Trim(), report);
                return (recorded, evaluator.Evaluate(basis!.Rules, RemedyScenario.RepairFailed, repair.FailedAttempts));
            }

            default:
                throw new InvalidClaimTransitionException($"Cannot record {type} on a repair in status {repair.Status}.");
        }
    }

    /// <summary>
    /// Writes the <see cref="RemedyDecisionKind.Evaluated"/> row of an ended attempt and, unless the repair continues,
    /// returns the remedy to a reviewer with the evaluation's allowed remedies and pending reason (FR-031, FR-032).
    /// </summary>
    private async Task ReEvaluateAsync(
        Guid claimId, ClaimRemedy remedy, Repair repair, RemedyEvaluation evaluation, RemedyBasis basis, DateTimeOffset now,
        CancellationToken ct)
    {
        var decisions = await remedies.GetDecisionsAsync(claimId, ct);
        var seq = decisions.Count == 0 ? 1 : decisions.Max(d => d.Seq) + 1;
        var estimate = evaluation.Scenario == RemedyScenario.RepairNotEconomical ? repair.CurrentAttempt?.EstimateAmount : null;
        remedies.AddDecision(RemedyDecision.Evaluated(
            Guid.CreateVersion7(now), tenant.TenantId, claimId, seq, evaluation.Scenario, basis.PolicyVersionId, evaluation.TableRow,
            evaluation.AllowedRemedies, repair.FailedAttempts, estimate, now));

        if (!evaluation.ContinueRepair)
        {
            remedy.ReturnToDecision(
                basis.PolicyVersionId, evaluation.Scenario, evaluation.AllowedRemedies,
                evaluation.PendingReason ?? RemedyPendingReason.NoRemedyRule, now);
        }
    }

    private Task AppendTrailAsync(
        Guid claimId, Repair repair, RepairEvent recorded, RemedyEvaluation? evaluation, RemedyBasis? basis, CancellationToken ct)
    {
        var (step, summary) = recorded.Type switch
        {
            RepairEventType.CentreChanged => (TrailStep.RepairCentreAssigned, "Repair centre changed by staff."),
            RepairEventType.HandedOver => (TrailStep.RepairHandedOver, "Product handed over to the repair centre."),
            RepairEventType.MarkedReady => (TrailStep.RepairStatusUpdated, "Repair ready for collection."),
            RepairEventType.Collected => (TrailStep.RepairStatusUpdated, "Repaired product collected."),
            RepairEventType.EstimateReported when evaluation is not null => (TrailStep.RepairAttemptEnded, AttemptEndedSummary(evaluation)),
            RepairEventType.EstimateReported => (TrailStep.RepairStatusUpdated, "Repair estimate reported by the repair centre."),
            RepairEventType.RepairFailed => (TrailStep.RepairAttemptEnded, AttemptEndedSummary(evaluation!)),
            RepairEventType.NewAttemptStarted => (TrailStep.RepairStatusUpdated, "New repair attempt started."),
            RepairEventType.Transferred => (TrailStep.RepairCentreAssigned, "Repair transferred to another repair centre."),
            RepairEventType.Correction => (TrailStep.RepairStatusUpdated, "A repair entry was corrected; the original entry is kept."),
            var other => (TrailStep.RepairStatusUpdated, $"Repair update recorded: {other}."),
        };

        var handover = repair.ActiveHandover;
        var attempt = repair.CurrentAttempt;
        var payload = new Dictionary<string, object?>
        {
            ["repairId"] = repair.Id,
            ["eventSeq"] = recorded.Seq,
            ["type"] = recorded.Type.ToString(),
            ["fromStatus"] = recorded.FromStatus?.ToString(),
            ["toStatus"] = recorded.ToStatus?.ToString(),
            ["reportedAt"] = recorded.ReportedAt,
            ["recordedAt"] = recorded.RecordedAt,
            ["source"] = recorded.Source.ToString(),
            ["serviceCenterId"] = handover?.ServiceCenterId,
            ["handedOverOn"] = recorded.Type == RepairEventType.HandedOver ? handover?.HandedOverOn : null,
            ["attemptId"] = repair.CurrentAttemptId,
            ["reason"] = recorded.Type is RepairEventType.CentreChanged or RepairEventType.Transferred ? handover?.Reason : null,
        };

        if (recorded.Type == RepairEventType.Correction)
        {
            payload["correctsSeq"] = CorrectsSeqOf(recorded);
            payload["reason"] = CorrectionReasonOf(recorded);
        }

        if (recorded.Type is RepairEventType.EstimateReported or RepairEventType.RepairFailed)
        {
            payload["attemptSeq"] = attempt?.Seq;
            payload["attemptResult"] = attempt?.Result.ToString();
            payload["estimateAmount"] = attempt?.EstimateAmount;
            payload["failedAttempts"] = repair.FailedAttempts;
        }

        if (evaluation is not null && basis is not null)
        {
            payload["failureReason"] = attempt?.FailureReason;
            payload["policyCode"] = basis.PolicyCode;
            payload["policyVersion"] = basis.Version;
            payload["policyVersionId"] = basis.PolicyVersionId;
            payload["scenario"] = evaluation.Scenario.ToString();
            payload["tableRow"] = evaluation.TableRow;
            payload["attemptLimit"] = basis.Rules?.RepairFailed?.AfterFailedAttempts;
            payload["economicLimitAmount"] = evaluation.EconomicLimitAmount;
            payload["allowedRemedies"] = evaluation.AllowedRemedies.Select(r => r.ToString()).ToArray();
            payload["recommendedRemedy"] = evaluation.SingleRemedy?.ToString();
            payload["pendingReason"] = evaluation.PendingReason?.ToString();
            payload["newAttemptAllowed"] = evaluation.ContinueRepair;
        }

        return trail.AppendAsync(claimId, step, tenant.PrincipalId, summary, payload, ct);
    }

    private static int? CorrectsSeqOf(RepairEvent correction) => PayloadValue(correction, "correctsSeq")?.GetInt32();

    private static string? CorrectionReasonOf(RepairEvent correction) => PayloadValue(correction, "reason")?.GetString();

    private static JsonElement? PayloadValue(RepairEvent repairEvent, string property)
    {
        using var payload = JsonDocument.Parse(repairEvent.PayloadJson);
        return payload.RootElement.ValueKind == JsonValueKind.Object && payload.RootElement.TryGetProperty(property, out var value)
            ? value.Clone()
            : null;
    }

    private static string AttemptEndedSummary(RemedyEvaluation evaluation)
        => (evaluation.Scenario, evaluation.ContinueRepair) switch
        {
            (RemedyScenario.RepairFailed, true) => "Repair attempt failed; a new attempt is allowed.",
            (RemedyScenario.RepairFailed, false) => "Repair attempt failed at the attempt limit; the remedy goes to a reviewer.",
            _ => "Repair not economical: the estimate is above the policy's limit; the remedy goes to a reviewer.",
        };

    /// <summary>The claim's remedy rules (its remedy's policy version), the policy code for the trail and the claim value.</summary>
    private async Task<RemedyBasis> RemedyBasisAsync(Claim claim, ClaimRemedy remedy, CancellationToken ct)
    {
        var version = await policies.GetVersionAsync(remedy.PolicyVersionId, ct);
        var policy = version is null ? null : await policies.GetPolicyAsync(version.PolicyId, ct);
        var product = claim.ProductId is { } productId ? await catalog.GetProductAsync(productId, ct) : null;
        return new RemedyBasis(remedy.PolicyVersionId, version?.RemedyRules, policy?.Code, version?.Version, product?.ClaimValue);
    }

    private static bool IsRepairInProgress(ClaimRemedy? remedy)
        => remedy is { State: RemedyState.InProgress, Remedy: RemedyType.Repair };

    private async Task<bool> IsEligibleCentreAsync(Claim claim, Guid centreId, CancellationToken ct)
    {
        var product = claim.ProductId is { } productId ? await catalog.GetProductAsync(productId, ct) : null;
        var centres = await RepairCentreEligibility.ListAsync(network, claim, product, ct);
        return centres.Any(c => c.Id == centreId);
    }

    private static RecordRepairEventResult.Conflict NotAllowed(Repair repair, ClaimRemedy? remedy, RepairEventType type)
    {
        var allowed = AllowedEvents(repair, remedy);
        return new RecordRepairEventResult.Conflict(
            $"{type} is not allowed while the repair is {repair.Status}; allowed events: "
            + (allowed.Count == 0 ? "none" : string.Join(", ", allowed)) + ".",
            repair.Status,
            allowed);
    }

    /// <summary>Field errors of the request for its event type (contracts <c>RepairEventRequest</c>, FR-025).</summary>
    private static Dictionary<string, string[]> Validate(RecordRepairEventCommand command, Repair repair, DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Add(string field, string message) => errors[field] = [message];

        if (command.Type is null)
        {
            Add("type", "The event type is required.");
        }

        if (command.Source is null)
        {
            Add("source", "The source of the report is required.");
        }
        else if (command.Source == ReportSource.System || !Enum.IsDefined(command.Source.Value))
        {
            Add("source", "The source must be Phone, Email, InPerson or Other.");
        }

        if (command.Note is { } note && note.Trim().Length > RepairHandover.MaxNoteLength)
        {
            Add("note", $"A note has at most {RepairHandover.MaxNoteLength} characters.");
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        switch (command.Type)
        {
            case RepairEventType.CentreChanged:
                if (command.RepairCentreId is null || command.RepairCentreId == Guid.Empty)
                {
                    Add("repairCentreId", "The repair centre is required.");
                }

                ValidateReason(command.Reason, Add);
                break;

            case RepairEventType.HandedOver:
                if (command.HandedOverOn is not { } handedOverOn)
                {
                    Add("handedOverOn", "The hand-over date is required.");
                }
                else if (handedOverOn > today)
                {
                    Add("handedOverOn", "The hand-over date must not be in the future.");
                }

                if (command.CentreReference is { } reference && reference.Trim().Length > RepairHandover.MaxCentreReferenceLength)
                {
                    Add("centreReference", $"A centre reference has at most {RepairHandover.MaxCentreReferenceLength} characters.");
                }

                break;

            case RepairEventType.EstimateReported:
                ValidateEstimate(command.EstimateAmount, Add);
                break;

            case RepairEventType.RepairFailed:
                ValidateReason(command.Reason, Add);
                break;

            case RepairEventType.NewAttemptStarted:
                if (command.ProductLocation is not { } location || !Enum.IsDefined(location))
                {
                    Add("productLocation", "Where the product is (AtCentre or ReturnedToCustomer) is required.");
                }

                break;

            case RepairEventType.Transferred:
                if (command.RepairCentreId is null || command.RepairCentreId == Guid.Empty)
                {
                    Add("repairCentreId", "The repair centre is required.");
                }

                ValidateReason(command.Reason, Add);
                break;

            case RepairEventType.Correction:
                ValidateCorrection(command, today, Add);
                break;
        }

        if (command.ReportedAt is not { } reportedAt)
        {
            Add("reportedAt", "The time the repair centre reported the update is required.");
        }
        else if (reportedAt > now)
        {
            Add("reportedAt", "The reported time must not be in the future.");
        }
        else if (HandoverDate(command, repair) is { } handoverDate && DateOnly.FromDateTime(reportedAt.UtcDateTime) < handoverDate)
        {
            Add("reportedAt", $"The reported time must not be before the hand-over on {handoverDate:yyyy-MM-dd}.");
        }

        return errors;
    }

    /// <summary>The hand-over date an update cannot be reported before: the active hand-over's, or the one being recorded.</summary>
    private static DateOnly? HandoverDate(RecordRepairEventCommand command, Repair repair)
        => command.Type == RepairEventType.HandedOver ? command.HandedOverOn : repair.ActiveHandover?.HandedOverOn;

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

    /// <summary>A correction names the corrected entry, a reason and exactly one corrected value (contracts <c>RepairEventRequest</c>).</summary>
    private static void ValidateCorrection(RecordRepairEventCommand command, DateOnly today, Action<string, string> add)
    {
        if (command.CorrectsSeq is not > 0)
        {
            add("correctsSeq", "The sequence number of the corrected entry is required.");
        }

        ValidateReason(command.Reason, add);
        var correction = new RepairCorrection(command.HandedOverOn, command.CentreReference, command.EstimateAmount, command.ToStatus);
        if (correction.ValueCount != 1)
        {
            add("correction", "A correction sets exactly one corrected value: handedOverOn, centreReference, estimateAmount or toStatus.");
            return;
        }

        if (command.HandedOverOn > today)
        {
            add("handedOverOn", "The hand-over date must not be in the future.");
        }

        if (command.CentreReference is { } reference && reference.Trim().Length > RepairHandover.MaxCentreReferenceLength)
        {
            add("centreReference", $"A centre reference has at most {RepairHandover.MaxCentreReferenceLength} characters.");
        }

        if (command.EstimateAmount is not null)
        {
            ValidateEstimate(command.EstimateAmount, add);
        }

        if (command.ToStatus is { } toStatus && !Enum.IsDefined(toStatus))
        {
            add("toStatus", "The status is not a repair status.");
        }
    }

    private static void ValidateEstimate(decimal? estimate, Action<string, string> add)
    {
        if (estimate is not { } amount)
        {
            add("estimateAmount", "The estimate amount is required.");
        }
        else if (amount < 0)
        {
            add("estimateAmount", "The estimate amount must not be negative.");
        }
        else if (amount > MaxEstimateAmount || decimal.Round(amount, 2) != amount)
        {
            add("estimateAmount", "The estimate amount has at most two decimals and at most ten digits before them.");
        }
    }

    /// <summary>What a re-evaluation reads: the remedy's policy version and its rules, the policy code and the claim value.</summary>
    private sealed record RemedyBasis(Guid PolicyVersionId, RemedyRules? Rules, string? PolicyCode, int? Version, decimal? ClaimValue);
}
