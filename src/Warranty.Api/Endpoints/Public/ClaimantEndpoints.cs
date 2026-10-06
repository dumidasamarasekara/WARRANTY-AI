using System.Security.Claims;
using Warranty.Api.Auth;
using Warranty.Api.RateLimiting;
using Warranty.Api.Tenancy;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Claims;

namespace Warranty.Api.Endpoints.Public;

/// <summary><c>ClaimantAccessRequest</c>: the claim reference and the email address or phone number given at submission.</summary>
public sealed record ClaimantAccessRequest(string? Reference, string? Contact);

/// <summary><c>PublicTenantInfo</c>: branding of the tenant behind the claimant channel Host.</summary>
public sealed record PublicTenantInfo(string DisplayName);

/// <summary>
/// Claimant access and status on a tenant's claimant channel (FR-037, FR-037a, research R10). The
/// tenant is the channel Host's; a claimant token is accepted only for that tenant (checked by the
/// tenant resolution) and only for its own claim.
/// </summary>
public static class ClaimantEndpoints
{
    public const string AccessDeniedTitle = "Access denied";

    public const string AccessDeniedDetail = "The claim reference and the contact details do not match.";

    public static RouteGroupBuilder MapClaimantRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/tenant", GetTenantAsync)
            .WithName("GetPublicTenant")
            .WithSummary("Branding info for the tenant resolved from the Host")
            .AllowAnonymous()
            .Produces<PublicTenantInfo>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/claims/access", AccessAsync)
            .WithName("ClaimantAccess")
            .WithSummary("Exchange claim reference + submitted contact for a claim-scoped token")
            .AllowAnonymous()
            .Produces<ClaimantAccessToken>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireClaimantAccessLimit();

        group.MapGet("/claims/{reference}", GetClaimAsync)
            .WithName("GetClaimantClaim")
            .WithSummary("Claimant view of a claim")
            .RequireAuthorization(AuthPolicies.Claimant)
            .Produces<ClaimantClaimView>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> GetTenantAsync(ITenantContext tenant, ITenantRepository tenants, CancellationToken ct)
        => await tenants.GetActiveTenantAsync(tenant.TenantId, ct) is { } active
            ? Results.Ok(new PublicTenantInfo(active.DisplayName))
            : Results.Problem(statusCode: StatusCodes.Status404NotFound);

    /// <summary>The same 401 for an unknown reference and a contact that does not match (FR-037a).</summary>
    private static async Task<IResult> AccessAsync(
        ClaimantAccessRequest request, ClaimantAccess access, ClaimantTokenService tokens, ITenantContext tenant, CancellationToken ct)
    {
        var result = await access.VerifyAsync(request.Reference, request.Contact, ct);
        return result.ClaimId is { } claimId
            ? Results.Ok(tokens.Issue(tenant.TenantId, claimId))
            : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: AccessDeniedTitle, detail: AccessDeniedDetail);
    }

    /// <summary>404 for a reference that is unknown in the tenant or is not the token's claim.</summary>
    private static async Task<IResult> GetClaimAsync(string reference, ClaimsPrincipal user, ClaimantAccess access, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirst(TrustedClaimTypes.ClaimId)?.Value, out var claimId))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        return await access.GetViewAsync(claimId, reference, ct) is { } view
            ? Results.Ok(view)
            : Results.Problem(statusCode: StatusCodes.Status404NotFound);
    }
}
