using System.Globalization;
using System.Threading.RateLimiting;

namespace Warranty.Api.RateLimiting;

public static class RateLimitingExtensions
{
    /// <summary>
    /// Registers the rate limiter. Rejections are 429 ProblemDetails with <c>Retry-After</c> in seconds
    /// when the limiter knows it. The endpoint policies are added in T109.
    /// </summary>
    public static IServiceCollection AddWarrantyRateLimiting(this IServiceCollection services)
        => services.AddRateLimiter(options =>
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
        });
}
