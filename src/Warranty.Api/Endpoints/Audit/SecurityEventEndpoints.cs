using Warranty.Api.Auth;
using Warranty.Application.Audit;
using Warranty.Domain.Common;

namespace Warranty.Api.Endpoints.Audit;

/// <summary>Auditor route under <c>/api/security-events</c> (FR-041a); the tenant comes from the staff access token.</summary>
public static class SecurityEventEndpoints
{
    /// <summary>The kinds an auditor may filter by: every tenant-visible kind (operator-only kinds excluded).</summary>
    private static readonly string TenantVisibleKinds = string.Join(
        ", ", Enum.GetValues<SecurityEventKind>().Where(k => !k.IsOperatorOnly()).Select(k => WireName.Of(k)));

    public static RouteGroupBuilder MapSecurityEventRoutes(this RouteGroupBuilder group)
    {
        // GET /api/security-events: the tenant's security events, newest first (auditors only).
        group.MapGet("/security-events", ListAsync)
            .WithName("ListSecurityEvents")
            .WithSummary("Security events of the caller's tenant, newest first")
            .RequireAuthorization(AuthPolicies.Auditor)
            .Produces<SecurityEventPage>()
            .ProducesValidationProblem();
        return group;
    }

    /// <summary>400 for an unknown or operator-only kind, or a page or page size out of range (contract: page ≥ 1, page size 1–100).</summary>
    private static async Task<IResult> ListAsync(
        string? kind, int? page, int? pageSize, SecurityEventQuery query, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        SecurityEventKind? filter = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (WireName.TryParse<SecurityEventKind>(kind, out var parsed) && !parsed.IsOperatorOnly())
            {
                filter = parsed;
            }
            else
            {
                errors["kind"] = [$"Kind must be one of {TenantVisibleKinds}."];
            }
        }

        if (page is < 1)
        {
            errors["page"] = ["Page must be 1 or more."];
        }

        if (pageSize is < 1 or > SecurityEventQuery.MaxPageSize)
        {
            errors["pageSize"] = [$"Page size must be 1–{SecurityEventQuery.MaxPageSize}."];
        }

        return errors.Count > 0
            ? Results.ValidationProblem(errors)
            : Results.Ok(await query.ListAsync(filter, page ?? 1, pageSize ?? SecurityEventQuery.DefaultPageSize, ct));
    }
}
