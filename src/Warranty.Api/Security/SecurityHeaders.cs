using Warranty.Api.Http;

namespace Warranty.Api.Security;

/// <summary>
/// Response security headers and the CORS policy of the API. The API only serves JSON and evidence
/// bytes — the SPA is served by Vite — so its responses never need to run script, load sub-resources
/// or be framed. The SPA reads evidence with <c>fetch</c> and shows it through a blob object URL, so
/// the CSP here (which governs only documents rendered from API responses) does not affect it.
/// </summary>
public static class SecurityHeaders
{
    public const string CorsPolicyName = "warranty-spa";

    public const string ContentSecurityPolicy =
        "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    /// <summary>The staff UI and the tenant claimant channels, on any port and either scheme.</summary>
    public static readonly IReadOnlySet<string> AllowedOriginHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", "aurora.localhost", "borealis.localhost" };

    public static bool IsAllowedOrigin(string origin)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.AbsolutePath == "/"
            && string.IsNullOrEmpty(uri.UserInfo)
            && AllowedOriginHosts.Contains(uri.Host);

    /// <summary>
    /// Registers <see cref="CorsPolicyName"/>: only <see cref="AllowedOriginHosts"/> origins, no
    /// credentials (staff calls carry a bearer token, claimant calls a claimant token header).
    /// </summary>
    public static IServiceCollection AddWarrantyCors(this IServiceCollection services)
        => services.AddCors(options => options.AddPolicy(CorsPolicyName, policy => policy
            .SetIsOriginAllowed(IsAllowedOrigin)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithExposedHeaders(CorrelationId.HeaderName, "Retry-After", "Content-Disposition")
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

    /// <summary>
    /// Adds the security headers to every response, including error responses; a header an endpoint
    /// already set (e.g. evidence content's <c>Cache-Control</c>) is kept. Place it first in the pipeline.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
        => app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                if (string.IsNullOrEmpty(headers.CacheControl))
                {
                    headers.CacheControl = "no-store";
                }
                return Task.CompletedTask;
            });
            return next(context);
        });
}
