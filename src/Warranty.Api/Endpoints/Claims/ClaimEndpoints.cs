using Warranty.Api.Auth;
using Warranty.Application.Claims;
using Warranty.Domain.Claims;

namespace Warranty.Api.Endpoints.Claims;

/// <summary>Staff claim routes under <c>/api/claims</c>; the tenant comes from the staff access token.</summary>
public static class ClaimEndpoints
{
    /// <summary><c>POST /api/claims</c>: a claims agent submits a claim on a customer's behalf (FR-007).</summary>
    public static RouteGroupBuilder MapClaimRoutes(this RouteGroupBuilder group)
    {
        group.MapPost(string.Empty,(HttpRequest request, SubmitClaim submitClaim, CancellationToken ct)
                => ClaimSubmissionRequest.SubmitAsync(
                    request,
                    submitClaim,
                    ClaimChannel.AgentPortal,
                    accepted => Results.Accepted(
                        $"/api/claims/{accepted.ClaimId}",
                        new SubmissionAccepted(accepted.ClaimId, accepted.Reference, accepted.Status, accepted.Round)),
                    ct))
            .WithName("SubmitClaimAsAgent")
            .WithSummary("Claims agent submits a claim on a customer's behalf")
            .RequireAuthorization(AuthPolicies.ClaimsAgent)
            .WithSubmissionLimits();
        return group;
    }
}
