using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warranty.Api.Tenancy;

namespace Warranty.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for Keycloak's JWT bearer handler on the staff scheme. <c>Authorization: Bearer
/// test-staff:{username}</c> authenticates as that seeded user with the claims a realm access token
/// carries (<c>sub</c>, <c>preferred_username</c>, <c>tenant_id</c>, realm roles); any other bearer
/// value (e.g. a claimant token) is left to the other schemes.
/// </summary>
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string TokenPrefix = "test-staff:";

    public static string TokenFor(TestStaffUser user) => TokenPrefix + user.Username;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer + TokenPrefix, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var username = header[(bearer.Length + TokenPrefix.Length)..];
        var user = TestStaffUsers.All.SingleOrDefault(u => u.Username == username);
        if (user is null)
        {
            return Task.FromResult(AuthenticateResult.Fail($"Unknown test user '{username}'."));
        }

        var claims = new List<Claim>
        {
            new(TrustedClaimTypes.Subject, user.Subject),
            new(TrustedClaimTypes.PreferredUsername, user.Username),
            new(TrustedClaimTypes.TenantId, user.TenantId.ToString()),
        };
        claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var identity = new ClaimsIdentity(claims, Scheme.Name, TrustedClaimTypes.PreferredUsername, ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
