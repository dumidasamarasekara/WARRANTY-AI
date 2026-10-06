using Warranty.Api.Auth;
using Warranty.Application.Review;

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
        return group;
    }

    /// <summary><c>GET /api/review-queue</c>: claims reviewers only; oldest escalation first.</summary>
    private static async Task<IResult> GetQueueAsync(ReviewQueueQuery query, CancellationToken ct)
        => Results.Ok(await query.ListAsync(ct));
}
