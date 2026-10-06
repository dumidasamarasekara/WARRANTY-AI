using Warranty.Api.Endpoints.Claims;
using Warranty.Api.Endpoints.Public;
using Warranty.Api.Endpoints.Reference;
using Warranty.Api.Endpoints.Trace;

namespace Warranty.Api.Endpoints;

/// <summary>
/// The API's endpoint groups per contracts/rest-api.openapi.yaml. Each user story maps its routes
/// into the matching group. Staff groups require a staff user (default policy <c>AnyStaff</c>) and
/// routes add their specific policy; public routes are anonymous unless they require the
/// <c>Claimant</c> policy.
/// </summary>
public static class EndpointGroups
{
    /// <summary>Claimant channel: tenant from the request Host.</summary>
    public static RouteGroupBuilder MapPublicEndpoints(this IEndpointRouteBuilder app)
        => app.MapGroup("/api/public").WithTags("Public")
            .MapPublicClaimRoutes()
            .MapClaimantRoutes();

    public static RouteGroupBuilder MapClaimEndpoints(this IEndpointRouteBuilder app)
        => app.MapGroup("/api/claims").WithTags("Claims").RequireAuthorization()
            .MapClaimRoutes();

    /// <summary><c>/api/review-queue</c> and <c>/api/claims/{claimId}/review-decisions</c>.</summary>
    public static RouteGroupBuilder MapReviewEndpoints(this IEndpointRouteBuilder app)
        => app.MapGroup("/api").WithTags("Review").RequireAuthorization();

    /// <summary><c>/api/claims/{claimId}/trace</c>.</summary>
    public static RouteGroupBuilder MapTraceEndpoints(this IEndpointRouteBuilder app)
        => app.MapGroup("/api/claims").WithTags("Trace").RequireAuthorization()
            .MapTraceRoutes();

    /// <summary><c>/api/me</c> and <c>/api/policies</c>.</summary>
    public static RouteGroupBuilder MapReferenceEndpoints(this IEndpointRouteBuilder app)
        => app.MapGroup("/api").WithTags("Reference").RequireAuthorization()
            .MapMeRoutes();

    /// <summary><c>/api/security-events</c>.</summary>
    public static RouteGroupBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
        => app.MapGroup("/api").WithTags("Audit").RequireAuthorization();
}
