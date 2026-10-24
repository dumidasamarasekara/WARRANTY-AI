using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
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
/// visible in the tenant (404); the request fields must be valid for the event type and <c>reportedAt</c> neither in
/// the future nor before the hand-over (400); <c>If-Match</c> must equal the repair's row version (412); a repair centre
/// must be an active eligible centre of the tenant (404 + <c>ACCESS_DENIED</c>); the event must be allowed from the
/// current status (409 naming the status and the allowed events). One transaction then writes the repair, its
/// <c>audit.repair_events</c> row and a trail entry; <c>MarkedReady</c> queues the <c>RepairReadyForCollection</c>
/// notification with the dedupe key <c>repair-ready:{attemptId}</c>, and <c>Collected</c> completes the remedy.
/// Pending T239 (US2 events); T248 and T256 add the US3 and US5 events.
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
    public Task<RecordRepairEventResult> ExecuteAsync(RecordRepairEventCommand command, CancellationToken ct)
        => throw new NotImplementedException(
            $"Pending T239 ({tenant}, {claims}, {catalog}, {remedies}, {repairs}, {network}, {notifications}, {trail}, "
            + $"{securityEvents}, {unitOfWork}, {time}, {command}, {ct}).");
}
