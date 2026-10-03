using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Warranty.Api.Tenancy;

namespace Warranty.Api.Auth;

/// <summary>Configuration of claimant tokens (section <c>ClaimantTokens</c>; the key belongs in user secrets).</summary>
public sealed class ClaimantTokenOptions
{
    public const string SectionName = "ClaimantTokens";

    /// <summary>HMAC-SHA256 signing key, at least 32 bytes of UTF-8.</summary>
    public string? SigningKey { get; set; }
}

/// <summary>A claim-scoped access token handed to a claimant after a successful access check.</summary>
public sealed record ClaimantAccessToken(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues and defines validation of the API-signed claimant tokens (research R10): HS256, issuer
/// <see cref="Issuer"/>, audience <see cref="Audience"/>, 30-minute lifetime, claims <c>sub =
/// claimant:{claimId}</c>, <c>tenant_id</c>, <c>claim_id</c> and <c>scope = claimant</c>. The
/// <see cref="TrustedClaimTypes.ClaimantScheme"/> bearer scheme validates them with
/// <see cref="ValidationParameters"/>, so a Keycloak token is never accepted as a claimant token and a
/// claimant token never passes the staff scheme.
/// </summary>
internal sealed class ClaimantTokenService
{
    public const string Issuer = "warranty-api";

    public const string Audience = "warranty-claimant";

    public const int MinimumKeyBytes = 32;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    internal static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };
    private readonly SigningCredentials _credentials;
    private readonly TimeProvider _time;

    public ClaimantTokenService(IOptions<ClaimantTokenOptions> options, TimeProvider time)
        : this(KeyFrom(options.Value.SigningKey), time)
    {
    }

    private ClaimantTokenService(SymmetricSecurityKey key, TimeProvider time)
    {
        _time = time;
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        ValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = key,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            NameClaimType = TrustedClaimTypes.Subject,
            LifetimeValidator = IsWithinLifetime,
        };
    }

    public TokenValidationParameters ValidationParameters { get; }

    /// <summary>A service with a random key, for local development without a configured key.</summary>
    internal static ClaimantTokenService WithEphemeralKey(TimeProvider time)
        => new(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(MinimumKeyBytes)), time);

    public ClaimantAccessToken Issue(Guid tenantId, Guid claimId)
    {
        if (tenantId == Guid.Empty || claimId == Guid.Empty)
        {
            throw new ArgumentException("A claimant token needs a tenant and a claim.");
        }

        var now = _time.GetUtcNow();
        var expires = now + Lifetime;
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = _credentials,
            Claims = new Dictionary<string, object>
            {
                [TrustedClaimTypes.Subject] = $"claimant:{claimId}",
                [TrustedClaimTypes.TenantId] = tenantId.ToString(),
                [TrustedClaimTypes.ClaimId] = claimId.ToString(),
                [TrustedClaimTypes.Scope] = TrustedClaimTypes.ClaimantScope,
            },
        });

        // JWT times have whole-second precision.
        return new ClaimantAccessToken(token, DateTimeOffset.FromUnixTimeSeconds(expires.ToUnixTimeSeconds()));
    }

    private bool IsWithinLifetime(DateTime? notBefore, DateTime? expires, SecurityToken token, TokenValidationParameters parameters)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return expires is { } exp
               && now <= exp.ToUniversalTime() + ClockSkew
               && (notBefore is not { } nbf || now >= nbf.ToUniversalTime() - ClockSkew);
    }

    internal static bool IsValidKey(string? signingKey)
        => !string.IsNullOrEmpty(signingKey) && Encoding.UTF8.GetByteCount(signingKey) >= MinimumKeyBytes;

    private static SymmetricSecurityKey KeyFrom(string? signingKey)
    {
        if (!IsValidKey(signingKey))
        {
            throw new InvalidOperationException(
                $"'{ClaimantTokenOptions.SectionName}:{nameof(ClaimantTokenOptions.SigningKey)}' must be at least {MinimumKeyBytes} bytes; set it in user secrets.");
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey!));
    }
}
