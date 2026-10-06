using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Warranty.Api.Auth;
using Warranty.Api.Tenancy;
using Warranty.Application.Claims;
using Warranty.Application.Review;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.Api.Endpoints.Review;

/// <summary>Human review of escalated claims of the reviewer's tenant (FR-032 – FR-036).</summary>
public static class ReviewEndpoints
{
    /// <summary>Maps the review routes onto the <c>/api</c> review group.</summary>
    public static RouteGroupBuilder MapReviewRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/review-queue", GetQueueAsync)
            .WithName("GetReviewQueue")
            .WithSummary("Escalated claims of the reviewer's tenant (FR-032)")
            .RequireAuthorization(AuthPolicies.ClaimsReviewer)
            .Produces<IReadOnlyList<ReviewQueueItem>>();

        group.MapPost("/claims/{claimId:guid}/review-decisions", RecordDecisionAsync)
            .WithName("RecordReviewDecision")
            .WithSummary("Record a reviewer decision (FR-034 – FR-036)")
            .RequireAuthorization(AuthPolicies.ClaimsReviewer)
            .Accepts<ReviewDecisionRequest>("application/json")
            .Produces<ReviewDecisionView>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);
        return group;
    }

    /// <summary><c>GET /api/review-queue</c>: claims reviewers only; oldest escalation first.</summary>
    private static async Task<IResult> GetQueueAsync(ReviewQueueQuery query, CancellationToken ct)
        => Results.Ok(await query.ListAsync(ct));

    /// <summary>
    /// <c>POST /api/claims/{claimId}/review-decisions</c>: claims reviewers only. The reviewer comes from
    /// the access token, the expected claim version from <c>If-Match</c> (the claim detail's ETag).
    /// </summary>
    private static async Task<IResult> RecordDecisionAsync(
        Guid claimId,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        ReviewDecisionRequest request,
        RecordReviewDecision recordDecision,
        CancellationToken ct)
    {
        if (!WireName.TryParse<ReviewDecisionKind>(request.Decision, out var kind))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["decision"] = [$"Decision must be one of {string.Join(", ", WireName.All<ReviewDecisionKind>())}."],
            });
        }

        var command = new RecordReviewDecisionCommand(
            claimId, ifMatch, kind, request.Justification, request.ClaimantExplanation, request.RequestedItems);

        return await recordDecision.ExecuteAsync(command, ct) switch
        {
            RecordReviewDecisionResult.Recorded recorded => Results.Created(
                $"/api/claims/{claimId}",
                new ReviewDecisionView(
                    recorded.Decision.Id,
                    recorded.Decision.Decision,
                    recorded.Decision.Justification,
                    recorded.Decision.ClaimantExplanation,
                    recorded.Decision.RequestedItems,
                    recorded.Decision.OverridesAi,
                    recorded.Decision.ReviewerName,
                    recorded.Decision.DecidedAt)),
            RecordReviewDecisionResult.NotFound => CrossTenantGuard.NotFound(),
            RecordReviewDecisionResult.SelfReviewRefused refused
                => Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: refused.Detail),
            RecordReviewDecisionResult.PreconditionFailed failed
                => Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed, detail: failed.Detail),
            RecordReviewDecisionResult.Conflict conflict
                => Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: conflict.Detail),
            RecordReviewDecisionResult.Invalid invalid => Results.ValidationProblem(new Dictionary<string, string[]>(invalid.Errors)),
            var other => throw new InvalidOperationException($"Unhandled review decision result {other.GetType().Name}."),
        };
    }
}

/// <summary>
/// <c>ReviewDecisionRequest</c> (contracts/rest-api.openapi.yaml). Unknown members are rejected
/// (<c>additionalProperties: false</c>), so no reviewer or tenant field can be smuggled in.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewDecisionRequest(
    string? Decision,
    string? Justification,
    string? ClaimantExplanation,
    IReadOnlyList<RequestedItem>? RequestedItems);
