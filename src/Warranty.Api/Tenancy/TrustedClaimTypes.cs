namespace Warranty.Api.Tenancy;

/// <summary>
/// Claim types and scheme names the API trusts to establish who is calling and for which tenant
/// (research R9, R10). Their values only ever come from validated tokens — never from request bodies,
/// query strings, routes or model output.
/// </summary>
public static class TrustedClaimTypes
{
    /// <summary>The tenant of a staff access token (Keycloak user attribute) or of a claimant token.</summary>
    public const string TenantId = "tenant_id";

    /// <summary>The claim a claimant token grants access to.</summary>
    public const string ClaimId = "claim_id";

    public const string Scope = "scope";

    /// <summary>The <see cref="Scope"/> value of API-issued claimant tokens.</summary>
    public const string ClaimantScope = "claimant";

    public const string Subject = "sub";

    public const string PreferredUsername = "preferred_username";

    /// <summary>The authentication scheme of API-issued claimant tokens.</summary>
    public const string ClaimantScheme = "Claimant";
}
