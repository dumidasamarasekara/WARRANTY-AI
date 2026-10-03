using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Warranty.Api.Auth;
using Warranty.Api.Tenancy;

namespace Warranty.UnitTests.Api;

public sealed class AuthTests
{
    private const string SigningKey = "unit-test-claimant-signing-key-0123456789";
    private static readonly Guid Aurora = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Claimant_tokens_carry_tenant_claim_and_scope_and_validate_on_the_claimant_scheme()
    {
        using var provider = Services();
        var claimId = Guid.NewGuid();

        var token = provider.GetRequiredService<ClaimantTokenService>().Issue(Aurora, claimId);
        var result = await AuthenticateAsync(provider, token.AccessToken);

        token.ExpiresAt.ShouldBe(_time.GetUtcNow().AddMinutes(30));
        result.Succeeded.ShouldBeTrue(result.Failure?.Message);
        var principal = result.Principal!;
        principal.FindFirst("sub")!.Value.ShouldBe($"claimant:{claimId}");
        principal.FindFirst("tenant_id")!.Value.ShouldBe(Aurora.ToString());
        principal.FindFirst("claim_id")!.Value.ShouldBe(claimId.ToString());
        principal.FindFirst("scope")!.Value.ShouldBe("claimant");
    }

    [Fact]
    public async Task Claimant_tokens_expire_after_30_minutes()
    {
        using var provider = Services();
        var token = provider.GetRequiredService<ClaimantTokenService>().Issue(Aurora, Guid.NewGuid());

        _time.Advance(TimeSpan.FromMinutes(29));
        (await AuthenticateAsync(provider, token.AccessToken)).Succeeded.ShouldBeTrue();

        _time.Advance(TimeSpan.FromMinutes(2));
        (await AuthenticateAsync(provider, token.AccessToken)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Tokens_signed_with_another_key_or_by_another_issuer_are_rejected()
    {
        using var provider = Services();
        var otherKey = Services(signingKey: "a-different-claimant-signing-key-9876543210")
            .GetRequiredService<ClaimantTokenService>().Issue(Aurora, Guid.NewGuid());
        var keycloakLike = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "http://localhost:8080/realms/warranty",
            Audience = ClaimantTokenService.Audience,
            Expires = _time.GetUtcNow().AddMinutes(5).UtcDateTime,
            NotBefore = _time.GetUtcNow().UtcDateTime,
            IssuedAt = _time.GetUtcNow().UtcDateTime,
            Claims = new Dictionary<string, object> { ["tenant_id"] = Aurora.ToString(), ["scope"] = "claimant" },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256),
        });

        (await AuthenticateAsync(provider, otherKey.AccessToken)).Succeeded.ShouldBeFalse();
        (await AuthenticateAsync(provider, keycloakLike)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void A_short_or_missing_signing_key_fails_outside_development()
    {
        using var shortKey = Services(signingKey: "too-short", environment: Environments.Production);
        Should.Throw<OptionsValidationException>(() => shortKey.GetRequiredService<IOptions<ClaimantTokenOptions>>().Value);

        using var missing = Services(signingKey: null, environment: Environments.Production);
        Should.Throw<OptionsValidationException>(() => missing.GetRequiredService<IOptions<ClaimantTokenOptions>>().Value);
    }

    [Fact]
    public async Task Development_without_a_key_uses_an_ephemeral_one()
    {
        using var provider = Services(signingKey: null);

        var token = provider.GetRequiredService<ClaimantTokenService>().Issue(Aurora, Guid.NewGuid());

        (await AuthenticateAsync(provider, token.AccessToken)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Keycloak_realm_roles_map_to_role_claims_for_staff_roles_only()
    {
        var identity = new ClaimsIdentity(
            [new Claim("realm_access", """{"roles":["offline_access","claims-agent","default-roles-warranty","claims-reviewer"]}""")],
            "Bearer");

        KeycloakRoleMapper.AddRealmRoles(new ClaimsPrincipal(identity));

        identity.FindAll(identity.RoleClaimType).Select(c => c.Value).ShouldBe(["claims-agent", "claims-reviewer"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"roles":"auditor"}""")]
    [InlineData("""["auditor"]""")]
    public void Malformed_realm_access_grants_no_role(string realmAccess)
    {
        var identity = new ClaimsIdentity([new Claim("realm_access", realmAccess)], "Bearer");

        KeycloakRoleMapper.AddRealmRoles(new ClaimsPrincipal(identity));

        identity.FindAll(identity.RoleClaimType).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(AuthPolicies.ClaimsAgent, "claims-agent", true)]
    [InlineData(AuthPolicies.ClaimsAgent, "claims-reviewer", false)]
    [InlineData(AuthPolicies.ClaimsReviewer, "claims-reviewer", true)]
    [InlineData(AuthPolicies.ClaimsReviewer, "auditor", false)]
    [InlineData(AuthPolicies.Auditor, "auditor", true)]
    [InlineData(AuthPolicies.Auditor, "claims-agent", false)]
    [InlineData(AuthPolicies.ReviewerOrAuditor, "claims-reviewer", true)]
    [InlineData(AuthPolicies.ReviewerOrAuditor, "auditor", true)]
    [InlineData(AuthPolicies.ReviewerOrAuditor, "claims-agent", false)]
    [InlineData(AuthPolicies.AnyStaff, "claims-agent", true)]
    [InlineData(AuthPolicies.AnyStaff, "auditor", true)]
    [InlineData(AuthPolicies.AnyStaff, "offline_access", false)]
    public async Task Staff_policies_require_a_tenant_and_one_of_their_roles(string policy, string role, bool allowed)
    {
        using var provider = Services();
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        (await authorization.AuthorizeAsync(Staff(role), policy)).Succeeded.ShouldBe(allowed);
        (await authorization.AuthorizeAsync(Staff(role, withTenant: false), policy)).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task A_dual_role_user_satisfies_both_agent_and_reviewer_policies_and_the_default_is_any_staff()
    {
        using var provider = Services();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var user = Staff("claims-agent", "claims-reviewer");

        (await authorization.AuthorizeAsync(user, AuthPolicies.ClaimsAgent)).Succeeded.ShouldBeTrue();
        (await authorization.AuthorizeAsync(user, AuthPolicies.ClaimsReviewer)).Succeeded.ShouldBeTrue();
        (await authorization.AuthorizeAsync(user, AuthPolicies.Auditor)).Succeeded.ShouldBeFalse();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        options.DefaultPolicy.ShouldBeSameAs(options.GetPolicy(AuthPolicies.AnyStaff));
    }

    [Fact]
    public async Task The_claimant_policy_accepts_claimant_tokens_only_and_staff_policies_reject_them()
    {
        using var provider = Services();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var claimant = new ClaimsPrincipal(new ClaimsIdentity(
            [new("tenant_id", Aurora.ToString()), new("claim_id", Guid.NewGuid().ToString()), new("scope", "claimant")], "Claimant"));

        (await authorization.AuthorizeAsync(claimant, AuthPolicies.Claimant)).Succeeded.ShouldBeTrue();
        (await authorization.AuthorizeAsync(claimant, AuthPolicies.AnyStaff)).Succeeded.ShouldBeFalse();
        (await authorization.AuthorizeAsync(Staff("claims-agent"), AuthPolicies.Claimant)).Succeeded.ShouldBeFalse();
        provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value.GetPolicy(AuthPolicies.Claimant)!
            .AuthenticationSchemes.ShouldBe([TrustedClaimTypes.ClaimantScheme]);
    }

    private ServiceProvider Services(string? signingKey = SigningKey, string environment = "Development")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ClaimantTokens:SigningKey"] = signingKey,
        }).Build();
        var host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns(environment);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(host);
        services.AddSingleton<TimeProvider>(_time);
        services.AddWarrantyAuth(configuration, host);
        return services.BuildServiceProvider();
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(ServiceProvider provider, string token)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = $"Bearer {token}";
        return await context.AuthenticateAsync(TrustedClaimTypes.ClaimantScheme);
    }

    private static ClaimsPrincipal Staff(string role, bool withTenant = true) => Staff([role], withTenant);

    private static ClaimsPrincipal Staff(params string[] roles) => Staff(roles, withTenant: true);

    private static ClaimsPrincipal Staff(string[] roles, bool withTenant)
    {
        var claims = new List<Claim> { new("sub", "staff-sub-1") };
        if (withTenant)
        {
            claims.Add(new Claim("tenant_id", Aurora.ToString()));
        }

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }
}
