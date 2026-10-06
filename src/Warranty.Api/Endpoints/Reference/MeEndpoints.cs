using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Persistence;

namespace Warranty.Api.Endpoints.Reference;

/// <summary><c>Me</c>: the signed-in staff user, their tenant and their staff roles.</summary>
/// <param name="TenantCurrency">ISO 4217 code from the tenant settings; the currency of every claim value.</param>
public sealed record Me(string Sub, string Name, string TenantDisplayName, string TenantCurrency, IReadOnlyList<string> Roles);

/// <summary>
/// The current staff user (FR-043, research R9). Everything comes from the resolved tenant context —
/// the validated token — and the tenant's own rows; nothing is read from the request.
/// </summary>
public static class MeEndpoints
{
    /// <summary>The staff roles in the contract's order; other role claims are not reported.</summary>
    private static readonly string[] StaffRoles = [Principals.ClaimsAgentRole, Principals.ClaimsReviewerRole, Principals.AuditorRole];

    /// <summary><c>GET /api/me</c>: any staff user.</summary>
    public static RouteGroupBuilder MapMeRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/me", GetMeAsync)
            .WithName("GetMe")
            .WithSummary("Current staff user (tenant and roles from the token)")
            .Produces<Me>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        return group;
    }

    /// <summary>403 when the tenant stopped being active after the request's tenant was resolved.</summary>
    private static async Task<IResult> GetMeAsync(ITenantContext tenant, ITenantRepository tenants, CancellationToken ct)
    {
        if (await tenants.GetActiveTenantAsync(tenant.TenantId, ct) is not { } active)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden);
        }

        var settings = await tenants.GetCurrentSettingsAsync(ct);
        var roles = StaffRoles.Where(tenant.Roles.Contains).ToList();
        return Results.Ok(new Me(tenant.PrincipalId, tenant.PrincipalName, active.DisplayName, settings.Currency, roles));
    }
}
