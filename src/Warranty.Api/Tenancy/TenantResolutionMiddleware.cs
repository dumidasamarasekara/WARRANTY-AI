using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Warranty.Application.Abstractions.Audit;
using Warranty.Domain.Common;

namespace Warranty.Api.Tenancy;

/// <summary>
/// Establishes the tenant of each API request from trusted context only, between authentication and
/// authorization (research R9):
/// <list type="bullet">
/// <item><c>/api/public/*</c> (claimant channel): the request Host through the channel registry. An
/// unknown host is answered with 404 and recorded as an operator-only <c>UNKNOWN_CHANNEL</c> event.
/// A claimant token is honoured only when it was issued for the same tenant.</item>
/// <item>Other <c>/api/*</c> requests from an authenticated staff user: the token's <c>tenant_id</c>
/// claim, which must name one active tenant.</item>
/// </list>
/// Tenant values in the body, query string, route or other headers are never read. Unauthenticated
/// staff requests pass through unresolved and are refused by authorization.
/// </summary>
internal sealed class TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
{
    internal static readonly PathString ApiPath = "/api";

    internal static readonly PathString PublicPath = "/api/public";

    internal const string AnonymousActor = "anonymous";

    /// <summary>The principal of a claimant-channel request without a claimant token.</summary>
    internal const string ClaimantPrincipal = "claimant";

    public async Task InvokeAsync(HttpContext context, HttpTenantContext tenantContext, TenantDirectory directory)
    {
        var path = context.Request.Path;
        var proceed = true;
        if (path.StartsWithSegments(PublicPath))
        {
            proceed = await ResolveClaimantChannelAsync(context, tenantContext, directory);
        }
        else if (path.StartsWithSegments(ApiPath) && context.User.Identity?.IsAuthenticated == true && !IsClaimant(context.User))
        {
            proceed = await ResolveStaffAsync(context, tenantContext, directory);
        }

        if (proceed)
        {
            await next(context);
        }
    }

    private async Task<bool> ResolveClaimantChannelAsync(HttpContext context, HttpTenantContext tenantContext, TenantDirectory directory)
    {
        var ct = context.RequestAborted;
        var host = context.Request.Host.Host;
        var tenant = string.IsNullOrWhiteSpace(host) ? null : await directory.FindByChannelHostAsync(host, ct);
        if (tenant is null)
        {
            logger.LogWarning("Claimant request on unknown channel host {Host} refused.", host);
            await SecurityEvents(context).RecordOperatorEventAsync(
                SecurityEventKind.UnknownChannel, AnonymousActor, host, RequestDetails(context), ct);
            await WriteProblemAsync(context, StatusCodes.Status404NotFound);
            return false;
        }

        var principalId = ClaimantPrincipal;
        var claimant = await AuthenticateClaimantAsync(context);
        if (claimant is not null)
        {
            var claimId = claimant.FindFirst(TrustedClaimTypes.ClaimId)?.Value;
            if (SingleTenantId(claimant) != tenant.Id || !Guid.TryParse(claimId, out var claim))
            {
                // A claimant token of another tenant (or a malformed one) never reaches this tenant's data.
                logger.LogWarning("Claimant token not issued for the tenant of channel host {Host} refused.", host);
                await SecurityEvents(context).RecordOperatorEventAsync(
                    SecurityEventKind.CrossTenantAccessDenied, $"claimant:{claimId}", host, RequestDetails(context), ct);
                await WriteProblemAsync(context, StatusCodes.Status404NotFound);
                return false;
            }

            principalId = $"claimant:{claim}";
        }

        tenantContext.Resolve(tenant, principalId, ClaimantPrincipal, [], CorrelationId(context));
        return true;
    }

    private async Task<bool> ResolveStaffAsync(HttpContext context, HttpTenantContext tenantContext, TenantDirectory directory)
    {
        var user = context.User;
        var subject = user.FindFirst(TrustedClaimTypes.Subject)?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var tenantId = SingleTenantId(user);
        var tenant = tenantId is { } id ? await directory.FindActiveTenantAsync(id, context.RequestAborted) : null;
        if (tenant is null || string.IsNullOrWhiteSpace(subject))
        {
            logger.LogWarning("Staff request without a single active tenant or subject refused (tenant claim {TenantId}).", tenantId);
            await WriteProblemAsync(
                context, StatusCodes.Status403Forbidden, "The access token does not identify a user of an active tenant.");
            return false;
        }

        var name = user.FindFirst(TrustedClaimTypes.PreferredUsername)?.Value ?? user.Identity?.Name ?? subject;
        var roles = user.Identities
            .SelectMany(identity => identity.FindAll(identity.RoleClaimType))
            .Concat(user.FindAll(ClaimTypes.Role))
            .Select(claim => claim.Value);
        tenantContext.Resolve(tenant, subject, name, roles, CorrelationId(context));
        return true;
    }

    /// <summary>The claimant principal of the request when the claimant token scheme is registered and the token is valid.</summary>
    private static async Task<ClaimsPrincipal?> AuthenticateClaimantAsync(HttpContext context)
    {
        var schemes = context.RequestServices.GetService<IAuthenticationSchemeProvider>();
        if (schemes is null || await schemes.GetSchemeAsync(TrustedClaimTypes.ClaimantScheme) is null)
        {
            return null;
        }

        var result = await context.AuthenticateAsync(TrustedClaimTypes.ClaimantScheme);
        return result.Succeeded && IsClaimant(result.Principal) ? result.Principal : null;
    }

    private static bool IsClaimant(ClaimsPrincipal principal)
        => principal.HasClaim(TrustedClaimTypes.Scope, TrustedClaimTypes.ClaimantScope);

    /// <summary>The token's tenant, or null when it carries none, several or an unparsable one.</summary>
    private static Guid? SingleTenantId(ClaimsPrincipal principal)
    {
        var values = principal.FindAll(TrustedClaimTypes.TenantId).Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList();
        return values.Count == 1 && Guid.TryParse(values[0], out var id) && id != Guid.Empty ? id : null;
    }

    private static string CorrelationId(HttpContext context)
        => Activity.Current is { } activity ? activity.TraceId.ToHexString() : context.TraceIdentifier;

    private static object RequestDetails(HttpContext context)
        => new { method = context.Request.Method, path = context.Request.Path.Value };

    private static ISecurityEventWriter SecurityEvents(HttpContext context)
        => context.RequestServices.GetRequiredService<ISecurityEventWriter>();

    private static Task WriteProblemAsync(HttpContext context, int statusCode, string? detail = null)
        => Results.Problem(statusCode: statusCode, detail: detail).ExecuteAsync(context);
}
