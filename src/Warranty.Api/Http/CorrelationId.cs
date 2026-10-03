using System.Diagnostics;

namespace Warranty.Api.Http;

/// <summary>
/// The correlation ID of a request is its trace ID, so a response, its ProblemDetails, the tenant
/// context, decision-trail entries and OpenTelemetry traces can be joined on one value.
/// </summary>
public static class CorrelationId
{
    public const string HeaderName = "X-Correlation-Id";

    public const string ProblemDetailsExtension = "correlationId";

    public static string Of(HttpContext context)
        => Activity.Current is { } activity ? activity.TraceId.ToHexString() : context.TraceIdentifier;

    /// <summary>Adds <see cref="HeaderName"/> to every response, including error responses.</summary>
    public static IApplicationBuilder UseCorrelationIdHeader(this IApplicationBuilder app)
        => app.Use((context, next) =>
        {
            var id = Of(context);
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[HeaderName] = id;
                return Task.CompletedTask;
            });
            return next(context);
        });
}
