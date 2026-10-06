using Warranty.Api.Endpoints.Claims;
using Warranty.Api.RateLimiting;
using Warranty.Application.Claims;
using Warranty.Domain.Claims;

namespace Warranty.Api.Endpoints.Public;

/// <summary>Claim submission through a tenant's claimant channel (FR-007); the tenant comes from the request Host.</summary>
public static class PublicClaimEndpoints
{
    /// <summary><c>POST /api/public/claims</c>: anonymous; the response carries no internal IDs.</summary>
    public static RouteGroupBuilder MapPublicClaimRoutes(this RouteGroupBuilder group)
    {
        group.MapPost("/claims", (HttpRequest request, SubmitClaim submitClaim, CancellationToken ct)
                => ClaimSubmissionRequest.SubmitAsync(
                    request,
                    submitClaim,
                    ClaimChannel.ClaimantPortal,
                    accepted => Results.Accepted(
                        $"/api/public/claims/{accepted.Reference}",
                        new SubmissionAccepted(null, accepted.Reference, accepted.Status, accepted.Round)),
                    ct))
            .WithName("SubmitClaimAsClaimant")
            .WithSummary("Submit a claim through the tenant's claimant channel")
            .AllowAnonymous()
            .WithSubmissionLimits()
            .RequireClaimSubmissionLimit();
        return group;
    }
}
