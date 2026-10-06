using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Warranty.Domain.Claims;

namespace Warranty.Api.RateLimiting;

/// <summary>A fixed-window limit: <see cref="PermitLimit"/> requests per <see cref="Window"/>.</summary>
public sealed class FixedWindowLimit
{
    public int PermitLimit { get; set; }

    public TimeSpan Window { get; set; }
}

/// <summary>Limits of the public endpoints (section <c>RateLimiting</c>, research R21 and R10).</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary><c>POST /api/public/claims</c>: per client IP.</summary>
    public FixedWindowLimit ClaimSubmission { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromHours(1) };

    /// <summary><c>POST /api/public/claims/access</c>: per client IP and claim reference.</summary>
    public FixedWindowLimit ClaimantAccess { get; set; } = new() { PermitLimit = 5, Window = TimeSpan.FromMinutes(15) };
}

/// <summary>
/// The named rate-limiting policies of the public endpoints. Every request to a limited route counts,
/// whatever its outcome; a rejection is the 429 ProblemDetails with <c>Retry-After</c> written by
/// <see cref="RateLimitingExtensions"/>. The claimant access partition includes the claim reference
/// from the JSON body, which <see cref="ReadClaimantAccessReferenceAsync"/> reads before the limiter runs.
/// </summary>
public static class RateLimitPolicies
{
    public const string ClaimSubmission = "claim-submission";

    public const string ClaimantAccess = "claimant-access";

    /// <summary>Larger bodies are not parsed; they share one partition per IP (still limited).</summary>
    private const int MaxAccessBodyBytes = 8 * 1024;

    private const string UnreadableReference = "-";

    private static readonly object ReferenceItemKey = new();

    public static RouteHandlerBuilder RequireClaimSubmissionLimit(this RouteHandlerBuilder builder)
        => builder.RequireRateLimiting(ClaimSubmission).ProducesProblem(StatusCodes.Status429TooManyRequests);

    public static RouteHandlerBuilder RequireClaimantAccessLimit(this RouteHandlerBuilder builder)
        => builder.RequireRateLimiting(ClaimantAccess).ProducesProblem(StatusCodes.Status429TooManyRequests);

    internal static void AddWarrantyPolicies(this RateLimiterOptions options)
    {
        options.AddPolicy(ClaimSubmission, context =>
            FixedWindow(context, ClientIp(context), limits => limits.ClaimSubmission));

        options.AddPolicy(ClaimantAccess, context =>
            FixedWindow(
                context,
                $"{ClientIp(context)}|{context.Items[ReferenceItemKey] as string ?? UnreadableReference}",
                limits => limits.ClaimantAccess));
    }

    /// <summary>
    /// For a request to the claimant access route, reads the normalized claim reference from the
    /// buffered JSON body into the request items and rewinds the body for the endpoint.
    /// </summary>
    internal static async Task ReadClaimantAccessReferenceAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName != ClaimantAccess)
        {
            return;
        }

        var request = context.Request;
        request.EnableBuffering();
        var buffer = new byte[MaxAccessBodyBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length
            && (read = await request.Body.ReadAsync(buffer.AsMemory(length), context.RequestAborted)) > 0)
        {
            length += read;
        }

        request.Body.Position = 0;
        if (length > MaxAccessBodyBytes)
        {
            return;
        }

        try
        {
            using var body = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("reference", out var reference)
                && reference.ValueKind == JsonValueKind.String)
            {
                context.Items[ReferenceItemKey] = ClaimReference.Normalize(reference.GetString()!);
            }
        }
        catch (JsonException)
        {
            // The endpoint answers the malformed body; the request counts in the IP's unreadable partition.
        }
    }

    private static RateLimitPartition<string> FixedWindow(
        HttpContext context, string key, Func<RateLimitingOptions, FixedWindowLimit> select)
    {
        var limit = select(context.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value);
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit.PermitLimit,
            Window = limit.Window,
            QueueLimit = 0,
        });
    }

    private static string ClientIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
