using System.Security.Claims;
using System.Text.Json;
using Warranty.Application.Abstractions;

namespace Warranty.Api.Auth;

/// <summary>
/// Maps Keycloak realm roles (<c>realm_access.roles</c> in the access token) to role claims. Only the
/// platform's staff roles are mapped; Keycloak's built-in roles (<c>offline_access</c>,
/// <c>default-roles-*</c>) are ignored.
/// </summary>
internal static class KeycloakRoleMapper
{
    internal const string RealmAccessClaim = "realm_access";

    internal static readonly IReadOnlySet<string> StaffRoles = new HashSet<string>(StringComparer.Ordinal)
    {
        Principals.ClaimsAgentRole,
        Principals.ClaimsReviewerRole,
        Principals.AuditorRole,
    };

    public static void AddRealmRoles(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        var roles = identity.FindAll(RealmAccessClaim).SelectMany(claim => RolesOf(claim.Value)).Where(StaffRoles.Contains).Distinct().ToList();
        foreach (var role in roles)
        {
            if (!identity.HasClaim(identity.RoleClaimType, role))
            {
                identity.AddClaim(new Claim(identity.RoleClaimType, role));
            }
        }
    }

    private static IEnumerable<string> RolesOf(string realmAccess)
    {
        try
        {
            using var document = JsonDocument.Parse(realmAccess);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("roles", out var roles)
                && roles.ValueKind == JsonValueKind.Array)
            {
                return roles.EnumerateArray()
                    .Where(role => role.ValueKind == JsonValueKind.String)
                    .Select(role => role.GetString()!)
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // A malformed claim grants no role.
        }

        return [];
    }
}
