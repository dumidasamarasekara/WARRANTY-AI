using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Warranty.Api.Tenancy;
using Warranty.Application.Abstractions;

namespace Warranty.Api.Auth;

/// <summary>
/// Authorization policies of the API. Staff policies accept only Keycloak access tokens that carry a
/// tenant and one of the policy's realm roles; the claimant policy accepts only API-issued claimant
/// tokens. A user holding several roles satisfies every policy one of those roles allows (research R29).
/// </summary>
public static class AuthPolicies
{
    public const string ClaimsAgent = nameof(ClaimsAgent);

    public const string ClaimsReviewer = nameof(ClaimsReviewer);

    public const string Auditor = nameof(Auditor);

    public const string ReviewerOrAuditor = nameof(ReviewerOrAuditor);

    public const string AnyStaff = nameof(AnyStaff);

    /// <summary>A claimant token for one claim (<c>/api/public/claims/{reference}/...</c>).</summary>
    public const string Claimant = nameof(Claimant);

    internal static void Configure(AuthorizationOptions options)
    {
        options.AddPolicy(ClaimsAgent, Staff(Principals.ClaimsAgentRole));
        options.AddPolicy(ClaimsReviewer, Staff(Principals.ClaimsReviewerRole));
        options.AddPolicy(Auditor, Staff(Principals.AuditorRole));
        options.AddPolicy(ReviewerOrAuditor, Staff(Principals.ClaimsReviewerRole, Principals.AuditorRole));

        var anyStaff = Staff(Principals.ClaimsAgentRole, Principals.ClaimsReviewerRole, Principals.AuditorRole);
        options.AddPolicy(AnyStaff, anyStaff);

        // RequireAuthorization() without a policy name means "any staff user".
        options.DefaultPolicy = anyStaff;

        options.AddPolicy(Claimant, new AuthorizationPolicyBuilder(TrustedClaimTypes.ClaimantScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(TrustedClaimTypes.Scope, TrustedClaimTypes.ClaimantScope)
            .RequireClaim(TrustedClaimTypes.TenantId)
            .RequireClaim(TrustedClaimTypes.ClaimId)
            .Build());
    }

    private static AuthorizationPolicy Staff(params string[] roles)
        => new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(TrustedClaimTypes.TenantId)
            .RequireRole(roles)
            .Build();
}
