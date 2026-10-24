using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Warranty.Api.Auth;
using Warranty.Api.Tenancy;
using Warranty.Application.Fulfillment;
using Warranty.Domain.Fulfillment;

namespace Warranty.Api.Endpoints.Fulfillment;

/// <summary>
/// Remedies and the repair hand-over of a claim of the caller's tenant (002 contracts/rest-api.delta.openapi.yaml).
/// Mapped onto the <c>/api/claims</c> fulfillment group, which runs <see cref="CrossTenantGuard"/>.
/// </summary>
public static class FulfillmentEndpoints
{
    public static RouteGroupBuilder MapFulfillmentRoutes(this RouteGroupBuilder group)
    {
        // GET /api/claims/{claimId}/repair-centres: eligible centres, the first serving one suggested (FR-018, FR-019).
        group.MapGet("/{claimId:guid}/repair-centres", GetRepairCentresAsync)
            .WithName("ListRepairCentres")
            .WithSummary("Eligible repair centres for the claim (FR-018, FR-019)")
            .RequireAuthorization(AuthPolicies.AnyStaff)
            .Produces<IReadOnlyList<RepairCentreView>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // POST /api/claims/{claimId}/remedy-decisions: a reviewer decides or changes the remedy (FR-011, FR-013, FR-015).
        group.MapPost("/{claimId:guid}/remedy-decisions", RecordRemedyDecisionAsync)
            .WithName("RecordRemedyDecision")
            .WithSummary("Decide or change the remedy of an approved claim (FR-011, FR-013, FR-015)")
            .RequireAuthorization(AuthPolicies.ClaimsReviewer)
            .Accepts<RemedyDecisionRequest>("application/json")
            .Produces<RemedyView>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);

        // POST /api/claims/{claimId}/repair/events: one repair-centre update reported to staff (FR-019 – FR-028).
        group.MapPost("/{claimId:guid}/repair/events", RecordRepairEventAsync)
            .WithName("RecordRepairEvent")
            .WithSummary("Record a repair-centre update reported to staff (FR-019 – FR-033)")
            .RequireAuthorization(AuthPolicies.ClaimsAgentOrReviewer)
            .Accepts<RepairEventRequest>("application/json")
            .Produces<RepairView>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);

        // POST /api/claims/{claimId}/remedy-completions: a Refund completed (FR-035); Refund only since 003 research R12.
        group.MapPost("/{claimId:guid}/remedy-completions", RecordRemedyCompletionAsync)
            .WithName("RecordRemedyCompletion")
            .WithSummary("Record completion of a Refund remedy (FR-035; 003 research R12)")
            .RequireAuthorization(AuthPolicies.ClaimsAgentOrReviewer)
            .Accepts<RemedyCompletionRequest>("application/json")
            .Produces<RemedyView>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);

        // GET /api/claims/{claimId}/replacement-locations: eligible replacement locations, the first serving one suggested (003 FR-008, FR-009).
        group.MapGet("/{claimId:guid}/replacement-locations", GetReplacementLocationsAsync)
            .WithName("ListReplacementLocations")
            .WithSummary("Eligible replacement locations for the claim (003 FR-008, FR-009)")
            .RequireAuthorization(AuthPolicies.AnyStaff)
            .Produces<IReadOnlyList<RepairCentreView>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // POST /api/claims/{claimId}/replacement/events: one replacement update reported to staff (003 FR-009 – FR-028).
        group.MapPost("/{claimId:guid}/replacement/events", RecordReplacementEventAsync)
            .WithName("RecordReplacementEvent")
            .WithSummary("Record a replacement update (003 FR-009 – FR-028)")
            .RequireAuthorization(AuthPolicies.ClaimsAgentOrReviewer)
            .Accepts<ReplacementEventRequest>("application/json")
            .Produces<ReplacementView>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);
        return group;
    }

    private static async Task<IResult> GetRepairCentresAsync(Guid claimId, RepairQueries query, CancellationToken ct)
        => await query.GetRepairCentresAsync(claimId, ct) is { } centres ? Results.Ok(centres) : CrossTenantGuard.NotFound();

    /// <summary>
    /// <c>POST /api/claims/{claimId}/remedy-decisions</c>: claims reviewers only. The reviewer comes from the access token,
    /// the expected remedy version from <c>If-Match</c> (the remedy's ETag in the claim detail); the decision is applied
    /// through the ActionExecutor (research R4). Errors as in plan.md "Error mapping".
    /// </summary>
    private static async Task<IResult> RecordRemedyDecisionAsync(
        Guid claimId,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        RemedyDecisionRequest request,
        RecordRemedyDecision recordDecision,
        RemedyQueries queries,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var remedy = Parse<RemedyType>(request.Remedy, "remedy", errors);
        if (errors.Count == 0 && remedy is null)
        {
            errors["remedy"] = [$"remedy is required: one of {string.Join(", ", Enum.GetNames<RemedyType>())}."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var command = new RecordRemedyDecisionCommand(claimId, ifMatch, remedy!.Value, request.Reason);
        return await recordDecision.ExecuteAsync(command, ct) switch
        {
            RecordRemedyDecisionResult.Recorded => await queries.GetRemedyAsync(claimId, ct) is { } view
                ? Results.Created($"/api/claims/{claimId}", view)
                : throw new InvalidOperationException($"The remedy of claim {claimId} was decided but cannot be read back."),
            RecordRemedyDecisionResult.NotFound => CrossTenantGuard.NotFound(),
            RecordRemedyDecisionResult.SelfReviewRefused refused
                => Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: refused.Detail),
            RecordRemedyDecisionResult.PreconditionFailed failed
                => Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed, detail: failed.Detail),
            RecordRemedyDecisionResult.Conflict conflict
                => Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: conflict.Detail),
            RecordRemedyDecisionResult.Invalid invalid => Results.ValidationProblem(new Dictionary<string, string[]>(invalid.Errors)),
            var other => throw new InvalidOperationException($"Unhandled remedy decision result {other.GetType().Name}."),
        };
    }

    /// <summary>
    /// <c>POST /api/claims/{claimId}/repair/events</c>: claims agents and reviewers. The staff member comes from the
    /// access token, the expected repair version from <c>If-Match</c> (the repair's ETag in the claim detail).
    /// </summary>
    private static async Task<IResult> RecordRepairEventAsync(
        Guid claimId,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        RepairEventRequest request,
        RecordRepairEvent recordEvent,
        RepairQueries query,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var type = Parse<RepairEventType>(request.Type, "type", errors, allowed: RequestEventTypes);
        var source = Parse<ReportSource>(request.Source, "source", errors, allowed: RequestSources);
        var productLocation = Parse<ProductLocation>(request.ProductLocation, "productLocation", errors);
        var toStatus = Parse<RepairStatus>(request.ToStatus, "toStatus", errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var command = new RecordRepairEventCommand(
            claimId, ifMatch, type, request.ReportedAt, source, request.Note, request.RepairCentreId, request.Reason,
            request.HandedOverOn, request.CentreReference, request.EstimateAmount, productLocation, request.CorrectsSeq, toStatus);

        switch (await recordEvent.ExecuteAsync(command, ct))
        {
            case RecordRepairEventResult.Recorded:
                return await query.GetRepairAsync(claimId, ct) is { } view
                    ? Results.Created($"/api/claims/{claimId}", view)
                    : throw new InvalidOperationException($"The repair of claim {claimId} was recorded but cannot be read back.");
            case RecordRepairEventResult.NotFound:
                return CrossTenantGuard.NotFound();
            case RecordRepairEventResult.PreconditionFailed failed:
                return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed, detail: failed.Detail);
            case RecordRepairEventResult.Conflict conflict:
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    detail: conflict.Detail,
                    extensions: conflict.Status is { } status
                        ? new Dictionary<string, object?>
                        {
                            ["repairStatus"] = status.ToString(),
                            ["allowedEvents"] = conflict.AllowedEvents.Select(e => e.ToString()).ToArray(),
                        }
                        : null);
            case RecordRepairEventResult.Invalid invalid:
                return Results.ValidationProblem(new Dictionary<string, string[]>(invalid.Errors));
            case var other:
                throw new InvalidOperationException($"Unhandled repair event result {other.GetType().Name}.");
        }
    }

    /// <summary>
    /// <c>POST /api/claims/{claimId}/remedy-completions</c>: claims agents and reviewers. The staff member comes from the
    /// access token, the expected remedy version from <c>If-Match</c> (the remedy's ETag in the claim detail). Only a
    /// Refund is completed here (003 research R12); a Repair or a Replacement is named so that the use case can refuse
    /// it naming the event that completes it (<c>Collected</c> repair event, <c>HandedToCustomer</c> replacement event).
    /// </summary>
    private static async Task<IResult> RecordRemedyCompletionAsync(
        Guid claimId,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        RemedyCompletionRequest request,
        RecordRemedyCompletion recordCompletion,
        RemedyQueries queries,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var remedy = Parse<RemedyType>(request.Remedy, "remedy", errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var command = new RecordRemedyCompletionCommand(claimId, ifMatch, remedy, request.CompletedOn, request.Reference);
        switch (await recordCompletion.ExecuteAsync(command, ct))
        {
            case RecordRemedyCompletionResult.Recorded:
                return await queries.GetRemedyAsync(claimId, ct) is { } view
                    ? Results.Created($"/api/claims/{claimId}", view)
                    : throw new InvalidOperationException($"The remedy of claim {claimId} was completed but cannot be read back.");
            case RecordRemedyCompletionResult.NotFound:
                return CrossTenantGuard.NotFound();
            case RecordRemedyCompletionResult.Forbidden forbidden:
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: forbidden.Detail);
            case RecordRemedyCompletionResult.PreconditionFailed failed:
                return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed, detail: failed.Detail);
            case RecordRemedyCompletionResult.Conflict conflict:
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    detail: conflict.Detail,
                    extensions: conflict.State is { } state
                        ? new Dictionary<string, object?> { ["remedyState"] = state.ToString(), ["remedy"] = conflict.CurrentRemedy?.ToString() }
                        : null);
            case RecordRemedyCompletionResult.Invalid invalid:
                return Results.ValidationProblem(new Dictionary<string, string[]>(invalid.Errors));
            case var other:
                throw new InvalidOperationException($"Unhandled remedy completion result {other.GetType().Name}.");
        }
    }

    private static async Task<IResult> GetReplacementLocationsAsync(Guid claimId, ReplacementQueries query, CancellationToken ct)
        => await query.GetReplacementLocationsAsync(claimId, ct) is { } locations ? Results.Ok(locations) : CrossTenantGuard.NotFound();

    /// <summary>
    /// <c>POST /api/claims/{claimId}/replacement/events</c>: claims agents and reviewers; which events a role may record
    /// (reviewer-only events, self-review) is decided by the use case (403). The staff member comes from the access token,
    /// the expected replacement version from <c>If-Match</c> (<c>ReplacementView.etag</c>). Errors as in 003 plan.md
    /// "Error mapping".
    /// </summary>
    private static async Task<IResult> RecordReplacementEventAsync(
        Guid claimId,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        ReplacementEventRequest request,
        RecordReplacementEvent recordEvent,
        ReplacementQueries query,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var type = Parse<ReplacementEventType>(request.Type, "type", errors, allowed: RequestReplacementEventTypes);
        var source = Parse<ReportSource>(request.Source, "source", errors, allowed: RequestSources);
        var toStatus = Parse<ReplacementStatus>(request.ToStatus, "toStatus", errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var command = new RecordReplacementEventCommand(
            claimId, ifMatch, type, request.ReportedAt, source, request.Note, request.LocationId, request.Reason, request.ReturnedOn,
            request.SerialSeen, request.ProductId, request.SerialNumber, request.HandedOverOn, request.ReturnConfirmed, request.CorrectsSeq,
            toStatus);

        switch (await recordEvent.ExecuteAsync(command, ct))
        {
            case RecordReplacementEventResult.Recorded:
                return await query.GetReplacementAsync(claimId, ct) is { } view
                    ? Results.Created($"/api/claims/{claimId}", view)
                    : throw new InvalidOperationException($"The replacement of claim {claimId} was recorded but cannot be read back.");
            case RecordReplacementEventResult.NotFound:
                return CrossTenantGuard.NotFound();
            case RecordReplacementEventResult.Forbidden forbidden:
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: forbidden.Detail);
            case RecordReplacementEventResult.PreconditionFailed failed:
                return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed, detail: failed.Detail);
            case RecordReplacementEventResult.Conflict conflict:
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    detail: conflict.Detail,
                    extensions: conflict.Status is { } status
                        ? new Dictionary<string, object?>
                        {
                            ["replacementStatus"] = status.ToString(),
                            ["allowedEvents"] = conflict.AllowedEvents.Select(e => e.ToString()).ToArray(),
                        }
                        : null);
            case RecordReplacementEventResult.Invalid invalid:
                return Results.ValidationProblem(new Dictionary<string, string[]>(invalid.Errors));
            case var other:
                throw new InvalidOperationException($"Unhandled replacement event result {other.GetType().Name}.");
        }
    }

    /// <summary>
    /// The event types a request may name (<c>ReplacementEventRequest.type</c>): every type except the system-recorded
    /// <c>LocationSuggested</c> and <c>Cancelled</c>; the use case refuses those not recordable yet.
    /// </summary>
    private static readonly ReplacementEventType[] RequestReplacementEventTypes =
    [
        ReplacementEventType.LocationChanged, ReplacementEventType.ReturnRecorded, ReplacementEventType.ReturnAccepted,
        ReplacementEventType.ModelProposed, ReplacementEventType.ModelApproved, ReplacementEventType.ModelDeclined,
        ReplacementEventType.UnitReserved, ReplacementEventType.UnitRefused, ReplacementEventType.Moved,
        ReplacementEventType.HandedToCustomer, ReplacementEventType.NotAvailable, ReplacementEventType.Correction,
    ];

    /// <summary>The event types a request may name (<c>RepairEventRequest.type</c>): every type except <c>CentreSuggested</c> and <c>RepairNotEconomical</c>.</summary>
    private static readonly RepairEventType[] RequestEventTypes =
    [
        RepairEventType.CentreChanged, RepairEventType.HandedOver, RepairEventType.EstimateReported, RepairEventType.MarkedReady,
        RepairEventType.Collected, RepairEventType.RepairFailed, RepairEventType.NewAttemptStarted, RepairEventType.Transferred,
        RepairEventType.Correction,
    ];

    /// <summary>The sources staff may report (<c>System</c> is recorded only by the system).</summary>
    private static readonly ReportSource[] RequestSources = [ReportSource.Phone, ReportSource.Email, ReportSource.InPerson, ReportSource.Other];

    /// <summary>Parses an enum member by its exact name; a missing value stays null (the use case names required fields).</summary>
    private static T? Parse<T>(string? value, string field, Dictionary<string, string[]> errors, IReadOnlyList<T>? allowed = null)
        where T : struct, Enum
    {
        if (value is null)
        {
            return null;
        }

        var options = allowed ?? Enum.GetValues<T>();
        foreach (var option in options)
        {
            if (string.Equals(option.ToString(), value, StringComparison.Ordinal))
            {
                return option;
            }
        }

        errors[field] = [$"{field} must be one of {string.Join(", ", options)}."];
        return null;
    }
}

/// <summary>
/// <c>RepairEventRequest</c> (002 contracts). Unknown members are rejected (<c>additionalProperties: false</c>), so no
/// staff, tenant or status field can be smuggled in; which fields an event type needs is checked by the use case.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RepairEventRequest(
    string? Type,
    DateTimeOffset? ReportedAt,
    string? Source,
    string? Note,
    Guid? RepairCentreId,
    string? Reason,
    DateOnly? HandedOverOn,
    string? CentreReference,
    decimal? EstimateAmount,
    string? ProductLocation,
    int? CorrectsSeq,
    string? ToStatus);

/// <summary>
/// <c>RemedyDecisionRequest</c> (002 contracts). Unknown members are rejected (<c>additionalProperties: false</c>), so
/// no reviewer or tenant field can be smuggled in.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemedyDecisionRequest(string? Remedy, string? Reason);

/// <summary>
/// <c>RemedyCompletionRequest</c> (002 contracts). Unknown members are rejected (<c>additionalProperties: false</c>), so
/// no staff or tenant field can be smuggled in.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemedyCompletionRequest(string? Remedy, DateOnly? CompletedOn, string? Reference);

/// <summary>
/// <c>ReplacementEventRequest</c> (003 contracts). Unknown members are rejected (<c>additionalProperties: false</c>), so no
/// staff, tenant or status field can be smuggled in; which fields an event type needs is checked by the use case.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReplacementEventRequest(
    string? Type,
    DateTimeOffset? ReportedAt,
    string? Source,
    string? Note,
    Guid? LocationId,
    string? Reason,
    DateOnly? ReturnedOn,
    string? SerialSeen,
    Guid? ProductId,
    string? SerialNumber,
    DateOnly? HandedOverOn,
    bool? ReturnConfirmed,
    int? CorrectsSeq,
    string? ToStatus);
