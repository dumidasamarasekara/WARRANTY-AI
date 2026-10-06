using System.Globalization;
using System.Threading.RateLimiting;

namespace Warranty.Api.RateLimiting;

public static class RateLimitingExtensions
{
    /// <summary>
    /// Registers the rate limiter with the <see cref="RateLimitPolicies"/> and their limits from section
    /// <c>RateLimiting</c>. Rejections are 429 ProblemDetails with <c>Retry-After</c> in seconds when the
    /// limiter knows it.
    /// </summary>
    public static IServiceCollection AddWarrantyRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .Validate(
                limits => IsValid(limits.ClaimSubmission) && IsValid(limits.ClaimantAccess),
                "Rate limits need a positive PermitLimit and Window.")
            .ValidateOnStart();

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, detail: "Too many requests. Try again later.")
                    .ExecuteAsync(context.HttpContext);
            };
            options.AddWarrantyPolicies();
        });
    }

    /// <summary>Reads the claimant access partition key from the request body, then applies the rate limiter.</summary>
    public static IApplicationBuilder UseWarrantyRateLimiting(this IApplicationBuilder app)
        => app
            .Use(async (context, next) =>
            {
                await RateLimitPolicies.ReadClaimantAccessReferenceAsync(context);
                await next(context);
            })
            .UseRateLimiter();

    private static bool IsValid(FixedWindowLimit limit) => limit.PermitLimit > 0 && limit.Window > TimeSpan.Zero;
}
