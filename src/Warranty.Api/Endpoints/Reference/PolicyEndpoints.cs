using Warranty.Application.Policies;

namespace Warranty.Api.Endpoints.Reference;

/// <summary>The warranty policy versions of the staff user's tenant (read-only).</summary>
public static class PolicyEndpoints
{
    /// <summary><c>GET /api/policies</c>: every staff role.</summary>
    public static RouteGroupBuilder MapPolicyRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/policies", ListPoliciesAsync)
            .WithName("ListPolicyVersions")
            .WithSummary("Policy versions of the caller's tenant (read-only)")
            .Produces<IReadOnlyList<PolicyVersionSummary>>();
        return group;
    }

    private static async Task<IResult> ListPoliciesAsync(PolicyVersionsQuery query, CancellationToken ct)
        => Results.Ok(await query.ListAsync(ct));
}
