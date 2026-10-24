using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Claims;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;

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
/// Records a staff update of a repair (002 FR-019 – FR-028, FR-038, research R5 – R7). In order: the claim must be
/// visible in the tenant (404) and have a repair (409); the request fields must be valid for the event type and
/// <c>reportedAt</c> neither in the future nor before the hand-over (400); <c>If-Match</c> must equal the repair's row
/// version (412); a repair centre must be an eligible centre of the tenant (404 + <c>ACCESS_DENIED</c>); the event must
/// be allowed from the current status (409 naming the status and the allowed events). One transaction then writes the
/// repair, its <c>audit.repair_events</c> row and a trail entry; <c>MarkedReady</c> queues the
/// <c>RepairReadyForCollection</c> notification with the dedupe key <c>repair-ready:{attemptId}</c>, and
/// <c>Collected</c> completes the remedy. A concurrent change detected on save is a 409. The roles (claims-agent,
/// claims-reviewer) are enforced by the endpoint. US2 events; T248 and T256 add the US3 and US5 events.
/// </summary>
public sealed class RecordRepairEvent(
    ITenantContext tenant,
    IClaimRepository claims,
    ICatalogRepository catalog,
    IRemedyRepository remedies,
    IRepairRepository repairs,
    IServiceNetwork network,
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

    public const string NoRepairDetail = "This claim has no repair.";

    public const string MissingIfMatchDetail = "An If-Match header with the repair's ETag is required.";

    public const string PreconditionDetail = "The repair has changed since it was loaded; reload it and record the update again.";

    public const string ConcurrentChangeDetail = "The repair was changed by another request; reload it and record the update again.";

    /// <summary>The dedupe key of the ready notification of <paramref name="attemptId"/> (research R7).</summary>
    public static string ReadyDedupeKey(Guid attemptId) => $"repair-ready:{attemptId}";

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
        if (type == RepairEventType.CentreChanged && !await IsEligibleCentreAsync(claim, command.RepairCentreId!.Value, ct))
        {
            await securityEvents.RecordAsync(
                SecurityEventKind.AccessDenied, tenant.PrincipalName, command.RepairCentreId.Value.ToString(),
                new { claimId = claim.Id, field = "repairCentreId" }, ct);
            return new RecordRepairEventResult.NotFound();
        }

        if (!repair.AllowedEvents().Contains(type))
        {
            return NotAllowed(repair, type);
        }

        ClaimRemedy? remedy = null;
        if (type == RepairEventType.Collected)
        {
            remedy = await remedies.GetAsync(claim.Id, ct);
            if (remedy is not { State: RemedyState.InProgress, Remedy: RemedyType.Repair })
            {
                return new RecordRepairEventResult.Conflict(
                    "The claim's remedy is not a repair in progress, so the collection cannot complete it.",
                    repair.Status, repair.AllowedEvents());
            }
        }

        var report = new RepairReport(
            command.ReportedAt!.Value, command.Source!.Value, tenant.PrincipalId, command.Note, now, tenant.PrincipalName);
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(
                async token =>
                {
                    var recorded = Apply(repair, type, command, report);
                    repairs.AddEvent(recorded);
                    await AppendTrailAsync(claim.Id, repair, recorded, token);

                    if (type == RepairEventType.MarkedReady && repair.CurrentAttemptId is { } attemptId)
                    {
                        await notifications.EnqueueAsync(claim.Id, ReadyTemplate, ReadyDedupeKey(attemptId), token);
                    }

                    if (remedy is not null)
                    {
                        remedy.Complete(RemedyType.Repair, now);
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
            return NotAllowed(repair, type);
        }

        return new RecordRepairEventResult.Recorded(repair);
    }

    private static RepairEvent Apply(Repair repair, RepairEventType type, RecordRepairEventCommand command, RepairReport report)
        => type switch
        {
            RepairEventType.CentreChanged => repair.ChangeCentre(command.RepairCentreId!.Value, command.Reason!.Trim(), report),
            RepairEventType.HandedOver => repair.RecordHandover(command.HandedOverOn!.Value, command.CentreReference, report),
            RepairEventType.MarkedReady => repair.MarkReady(report),
            RepairEventType.Collected => repair.RecordCollection(report),
            _ => throw new InvalidClaimTransitionException($"Cannot record {type} on a repair in status {repair.Status}."),
        };

    private Task AppendTrailAsync(Guid claimId, Repair repair, RepairEvent recorded, CancellationToken ct)
    {
        var (step, summary) = recorded.Type switch
        {
            RepairEventType.CentreChanged => (TrailStep.RepairCentreAssigned, "Repair centre changed by staff."),
            RepairEventType.HandedOver => (TrailStep.RepairHandedOver, "Product handed over to the repair centre."),
            RepairEventType.MarkedReady => (TrailStep.RepairStatusUpdated, "Repair ready for collection."),
            RepairEventType.Collected => (TrailStep.RepairStatusUpdated, "Repaired product collected."),
            var other => (TrailStep.RepairStatusUpdated, $"Repair update recorded: {other}."),
        };

        var handover = repair.ActiveHandover;
        var payload = new
        {
            repairId = repair.Id,
            eventSeq = recorded.Seq,
            type = recorded.Type.ToString(),
            fromStatus = recorded.FromStatus?.ToString(),
            toStatus = recorded.ToStatus?.ToString(),
            reportedAt = recorded.ReportedAt,
            recordedAt = recorded.RecordedAt,
            source = recorded.Source.ToString(),
            serviceCenterId = handover?.ServiceCenterId,
            handedOverOn = recorded.Type == RepairEventType.HandedOver ? handover?.HandedOverOn : null,
            attemptId = repair.CurrentAttemptId,
            reason = recorded.Type == RepairEventType.CentreChanged ? handover?.Reason : null,
        };
        return trail.AppendAsync(claimId, step, tenant.PrincipalId, summary, payload, ct);
    }

    private async Task<bool> IsEligibleCentreAsync(Claim claim, Guid centreId, CancellationToken ct)
    {
        var product = claim.ProductId is { } productId ? await catalog.GetProductAsync(productId, ct) : null;
        var centres = await RepairCentreEligibility.ListAsync(network, claim, product, ct);
        return centres.Any(c => c.Id == centreId);
    }

    private static RecordRepairEventResult.Conflict NotAllowed(Repair repair, RepairEventType type)
    {
        var allowed = repair.AllowedEvents();
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
}
