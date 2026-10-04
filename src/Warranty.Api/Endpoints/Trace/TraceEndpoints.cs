using Warranty.Api.Auth;
using Warranty.Application.Trace;

namespace Warranty.Api.Endpoints.Trace;

/// <summary>The decision trace of a claim of the staff user's tenant (FR-038 – FR-041).</summary>
public static class TraceEndpoints
{
    /// <summary><c>GET /api/claims/{claimId}/trace</c>: claims reviewers and auditors only.</summary>
    public static RouteGroupBuilder MapTraceRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/{claimId:guid}/trace", GetTraceAsync)
            .WithName("GetDecisionTrace")
            .WithSummary("Chronological decision trail with AI execution steps")
            .RequireAuthorization(AuthPolicies.ReviewerOrAuditor)
            .Produces<DecisionTrace>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return group;
    }

    /// <summary>404 for an unknown claim and for a claim of another tenant alike: neither is visible here.</summary>
    private static async Task<IResult> GetTraceAsync(Guid claimId, DecisionTraceQuery query, CancellationToken ct)
        => await query.GetAsync(claimId, ct) is { } trace
            ? Results.Ok(trace)
            : Results.Problem(statusCode: StatusCodes.Status404NotFound);
}
