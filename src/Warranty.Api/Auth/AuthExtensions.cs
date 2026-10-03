using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warranty.Api.Tenancy;

namespace Warranty.Api.Auth;

public static class AuthExtensions
{
    /// <summary>Configuration section of the staff identity provider (Keycloak resource under Aspire).</summary>
    public const string KeycloakSection = "Keycloak";

    /// <summary>
    /// Registers staff authentication (default scheme: JWT bearer tokens of the Keycloak realm, authority
    /// resolved through Aspire service discovery from <c>Keycloak:ServiceName</c> and <c>Keycloak:Realm</c>,
    /// audience <c>Keycloak:Audience</c>, realm roles mapped to role claims), the
    /// <see cref="TrustedClaimTypes.ClaimantScheme"/> scheme for API-issued claimant tokens, and the
    /// <see cref="AuthPolicies"/>. Outside Development a claimant signing key must be configured; startup fails otherwise.
    /// </summary>
    public static IServiceCollection AddWarrantyAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var keycloak = configuration.GetSection(KeycloakSection);
        var serviceName = keycloak["ServiceName"] ?? "keycloak";
        var realm = keycloak["Realm"] ?? "warranty";
        var audience = keycloak["Audience"] ?? "warranty-api";

        services.TryAddSingleton(TimeProvider.System);
        var claimantOptions = services.AddOptions<ClaimantTokenOptions>().Bind(configuration.GetSection(ClaimantTokenOptions.SectionName));
        var signingKeyConfigured = !string.IsNullOrEmpty(
            configuration[$"{ClaimantTokenOptions.SectionName}:{nameof(ClaimantTokenOptions.SigningKey)}"]);
        if (signingKeyConfigured || !environment.IsDevelopment())
        {
            claimantOptions
                .Validate(
                    o => ClaimantTokenService.IsValidKey(o.SigningKey),
                    $"'{ClaimantTokenOptions.SectionName}:{nameof(ClaimantTokenOptions.SigningKey)}' must be at least {ClaimantTokenService.MinimumKeyBytes} bytes; set it in user secrets.")
                .ValidateOnStart();
            services.AddSingleton<ClaimantTokenService>();
        }
        else
        {
            // Tokens signed with a per-process key stop validating when the API restarts; claimants
            // simply request access again. Never used outside Development.
            services.AddSingleton(sp => ClaimantTokenService.WithEphemeralKey(sp.GetRequiredService<TimeProvider>()));
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddKeycloakJwtBearer(serviceName, realm, JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Audience = audience;
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = !environment.IsDevelopment();
                options.TokenValidationParameters.NameClaimType = TrustedClaimTypes.PreferredUsername;
                options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
                options.Events ??= new JwtBearerEvents();
                var validated = options.Events.OnTokenValidated;
                options.Events.OnTokenValidated = async context =>
                {
                    await validated(context);
                    if (context.Principal is { } principal)
                    {
                        KeycloakRoleMapper.AddRealmRoles(principal);
                    }
                };
            })
            .AddJwtBearer(TrustedClaimTypes.ClaimantScheme);

        services.AddOptions<JwtBearerOptions>(TrustedClaimTypes.ClaimantScheme)
            .Configure<ClaimantTokenService>((options, tokens) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = tokens.ValidationParameters;
            });

        services.AddAuthorization(AuthPolicies.Configure);
        return services;
    }
}
