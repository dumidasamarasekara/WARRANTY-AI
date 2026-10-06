using Warranty.Api.Auth;
using Warranty.Api.Tenancy;
using Warranty.Application.Claims;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.Api.Endpoints.Claims;

/// <summary>Staff claim routes under <c>/api/claims</c>; the tenant comes from the staff access token.</summary>
public static class ClaimEndpoints
{
    public static RouteGroupBuilder MapClaimRoutes(this RouteGroupBuilder group)
    {
        // GET /api/claims: the tenant's claims, newest first (every staff role).
        group.MapGet(string.Empty, ListClaimsAsync)
            .WithName("ListClaims")
            .WithSummary("List claims of the caller's tenant")
            .RequireAuthorization(AuthPolicies.AnyStaff)
            .Produces<ClaimSummaryPage>()
            .ProducesValidationProblem();

        // POST /api/claims: a claims agent submits a claim on a customer's behalf (FR-007).
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

        // GET /api/claims/{claimId}: the full staff view (FR-033), projected for claims agents (FR-005).
        group.MapGet("/{claimId:guid}", GetClaimAsync)
            .WithName("GetClaim")
            .WithSummary("Full staff view of a claim and its latest evaluation")
            .RequireAuthorization(AuthPolicies.AnyStaff)
            .Produces<ClaimDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return group;
    }

    /// <summary>400 for an unknown status or a page or page size out of range (contract: page ≥ 1, page size 1–100).</summary>
    private static async Task<IResult> ListClaimsAsync(
        string? status, int? page, int? pageSize, ClaimQueries query, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        ClaimStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (WireName.TryParse<ClaimStatus>(status, out var parsed))
            {
                filter = parsed;
            }
            else
            {
                errors["status"] = [$"Status must be one of {string.Join(", ", WireName.All<ClaimStatus>())}."];
            }
        }

        if (page is < 1)
        {
            errors["page"] = ["Page must be 1 or more."];
        }

        if (pageSize is < 1 or > ClaimQueries.MaxPageSize)
        {
            errors["pageSize"] = [$"Page size must be 1–{ClaimQueries.MaxPageSize}."];
        }

        return errors.Count > 0
            ? Results.ValidationProblem(errors)
            : Results.Ok(await query.ListAsync(filter, page ?? 1, pageSize ?? ClaimQueries.DefaultPageSize, ct));
    }

    /// <summary>The detail with its row version as ETag; 404 for an unknown claim and another tenant's claim alike.</summary>
    private static async Task<IResult> GetClaimAsync(Guid claimId, ClaimQueries query, HttpContext http, CancellationToken ct)
    {
        if (await query.GetDetailAsync(claimId, ct) is not { } result)
        {
            return CrossTenantGuard.NotFound();
        }

        http.Response.Headers.ETag = result.ETag;
        http.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(result.Detail);
    }
}
