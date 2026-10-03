using Microsoft.Extensions.Options;
using Warranty.AI.Gateway.Routing;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.AiOps;

namespace Warranty.AI.Gateway.Usage;

/// <summary>
/// Prices token usage from <c>AiGateway:Pricing</c> and records one <c>aiops.model_calls</c> row per
/// gateway call (FR-040, research R16). The row joins the caller's unit of work, so it is stored with
/// the harness step that made the call — including steps that end in failure. Calls without a tenant
/// (platform knowledge ingestion) are not recorded: model call rows always belong to a tenant.
/// </summary>
public sealed class UsageRecorder(IAiOpsRepository aiOps, IOptions<AiGatewayOptions> options)
{
    private const decimal CacheReadMultiplier = 0.1m;
    private const decimal CacheWriteMultiplier = 1.25m;
    private const decimal TokensPerMillion = 1_000_000m;

    /// <summary>Estimated USD cost; zero for a model without configured pricing (e.g. a local embedding model).</summary>
    public decimal EstimateCost(string model, int inputTokens, int outputTokens, int cacheReadTokens, int cacheWriteTokens)
    {
        if (!options.Value.Pricing.TryGetValue(model, out var price))
        {
            return 0m;
        }

        var cost = inputTokens * price.InputPerMTok
                   + outputTokens * price.OutputPerMTok
                   + cacheReadTokens * (price.CacheReadPerMTok ?? price.InputPerMTok * CacheReadMultiplier)
                   + cacheWriteTokens * (price.CacheWritePerMTok ?? price.InputPerMTok * CacheWriteMultiplier);
        return Math.Round(cost / TokensPerMillion, 6, MidpointRounding.AwayFromZero);
    }

    public void Record(
        AiCallContext context,
        string route,
        PromptRef? prompt,
        AiUsage usage,
        ModelCallStatus status,
        string? stopReason,
        string? error,
        DateTimeOffset startedAt)
    {
        if (context.TenantId == Guid.Empty)
        {
            return;
        }

        aiOps.AddModelCall(new ModelCall
        {
            Id = Guid.CreateVersion7(),
            TenantId = context.TenantId,
            ClaimId = context.ClaimId,
            RunId = context.RunId,
            Agent = context.Agent,
            Route = route,
            Provider = usage.Provider,
            Model = usage.Model,
            PromptId = prompt?.Id,
            PromptVersion = prompt?.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CacheReadTokens = usage.CacheReadTokens,
            CacheWriteTokens = usage.CacheWriteTokens,
            LatencyMs = (long)usage.Latency.TotalMilliseconds,
            EstimatedCost = usage.EstimatedCost,
            StopReason = stopReason,
            Status = status,
            Error = error is { Length: > 1000 } ? error[..1000] : error,
            CorrelationId = context.CorrelationId,
            StartedAt = startedAt,
        });
    }
}
