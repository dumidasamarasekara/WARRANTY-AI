using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Fulfillment;

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
/// Records a staff update of a replacement (003 FR-009 – FR-028, research R6, R7, R10, R12). Pending T326: roles
/// claims-agent and claims-reviewer (403 otherwise), <c>If-Match</c> on the replacement (412), location eligibility through
/// <see cref="IServiceNetwork"/> with <see cref="ServiceCenterPurpose.Replacement"/> (404 + <c>ACCESS_DENIED</c>), the serial
/// rules of FR-022 through <see cref="IReplacementSerialRegistry"/> and the catalog (400), one transaction writing the
/// replacement, its <c>audit.replacement_events</c> row and a trail entry; <c>UnitReserved</c> queues the
/// <see cref="ReadyTemplate"/> notification with the dedupe key <c>replacement-ready:{unitId}</c>, and
/// <c>HandedToCustomer</c> completes the remedy and registers the handed-out serial with its cover end.
/// </summary>
#pragma warning disable CS9113 // Stub: every dependency is read by the implementation in T326.
public sealed class RecordReplacementEvent(
    ITenantContext tenant,
    IClaimRepository claims,
    ICatalogRepository catalog,
    IRemedyRepository remedies,
    IReplacementRepository replacements,
    IReplacementSerialRegistry serials,
    IPolicyRepository policies,
    IServiceNetwork network,
    INotificationService notifications,
    IDecisionTrailWriter trail,
    ISecurityEventWriter securityEvents,
    IUnitOfWork unitOfWork,
    TimeProvider time)
#pragma warning restore CS9113
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

    /// <summary>The dedupe key of the ready notification of the reserved unit <paramref name="unitId"/> (research R7).</summary>
    public static string ReadyDedupeKey(Guid unitId) => $"replacement-ready:{unitId}";

    public Task<RecordReplacementEventResult> ExecuteAsync(RecordReplacementEventCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        throw new NotImplementedException($"Pending T326 ({command.Type}).");
    }
}
