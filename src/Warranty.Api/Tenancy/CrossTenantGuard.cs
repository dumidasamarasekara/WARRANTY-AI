using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Common;

namespace Warranty.Api.Tenancy;

/// <summary>
/// Endpoint filter for staff routes that name a claim (<c>{claimId}</c>) and possibly one of its
/// evidence items (<c>{evidenceId}</c>) (FR-005, FR-041a, research R30). Before the handler runs it
/// looks the IDs up in the actor's tenant (row-level security hides every other tenant). An ID that
/// is not visible is answered with the same 404 ProblemDetails the handlers return for a missing
/// resource, and recorded as one tenant-visible <c>ACCESS_DENIED</c> whose target is the ID as
/// supplied and whose details are the same for an unknown ID and another tenant's ID. Only when the ID
/// exists in another tenant is an operator-only <c>CROSS_TENANT_ACCESS_DENIED</c> recorded as well.
/// No data of either tenant is returned. Routes without a claim ID pass through untouched.
/// </summary>
internal sealed class CrossTenantGuard : IEndpointFilter
{
    internal const string ClaimIdRouteValue = "claimId";

    internal const string EvidenceIdRouteValue = "evidenceId";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var tenant = http.RequestServices.GetRequiredService<ITenantContext>();
        if (!tenant.IsResolved || RouteId(http, ClaimIdRouteValue) is not { } claimId)
        {
            return await next(context);
        }

        var ct = http.RequestAborted;
        var claims = http.RequestServices.GetRequiredService<IClaimRepository>();
        if (await claims.GetAsync(claimId, ct) is null)
        {
            return await DenyAsync(http, tenant, ScopedIdKind.Claim, claimId);
        }

        if (RouteId(http, EvidenceIdRouteValue) is { } evidenceId && await claims.GetEvidenceItemAsync(claimId, evidenceId, ct) is null)
        {
            return await DenyAsync(http, tenant, ScopedIdKind.Evidence, evidenceId);
        }

        return await next(context);
    }

    /// <summary>The 404 every staff handler returns for a claim or evidence item it cannot see.</summary>
    internal static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound);

    private static async Task<IResult> DenyAsync(HttpContext http, ITenantContext tenant, ScopedIdKind kind, Guid id)
    {
        var ct = http.RequestAborted;
        var events = http.RequestServices.GetRequiredService<ISecurityEventWriter>();
        var target = id.ToString();

        // The route template, not the path, so the details carry no ID and read alike for unknown and foreign IDs.
        var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        await events.RecordAsync(
            SecurityEventKind.AccessDenied, tenant.PrincipalName, target, new { method = http.Request.Method, route }, ct);
        await events.RecordCrossTenantDenialAsync(kind, id, tenant.PrincipalName, target, ct);
        return NotFound();
    }

    private static Guid? RouteId(HttpContext http, string name)
        => http.Request.RouteValues.TryGetValue(name, out var value) && Guid.TryParse(value?.ToString(), out var id) ? id : null;
}
