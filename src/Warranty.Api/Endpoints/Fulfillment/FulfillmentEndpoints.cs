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
        return group;
    }

    private static async Task<IResult> GetRepairCentresAsync(Guid claimId, RepairQueries query, CancellationToken ct)
        => await query.GetRepairCentresAsync(claimId, ct) is { } centres ? Results.Ok(centres) : CrossTenantGuard.NotFound();

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
