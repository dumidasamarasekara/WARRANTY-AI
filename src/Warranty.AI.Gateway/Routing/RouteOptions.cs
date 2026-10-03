namespace Warranty.AI.Gateway.Routing;

/// <summary>The <c>AiGateway</c> configuration section (contracts/ai-gateway.md).</summary>
public sealed class AiGatewayOptions
{
    public const string SectionName = "AiGateway";

    public const string LiveMode = "live";

    public const string ReplayMode = "replay";

    /// <summary><c>live</c> calls the routed providers; <c>replay</c> answers every chat route from recordings.</summary>
    public string Mode { get; set; } = LiveMode;

    /// <summary>Task routes by name: <c>extraction</c>, <c>vision</c>, <c>policy-reasoning</c>, <c>adjudication</c>, <c>embedding</c>.</summary>
    public Dictionary<string, RouteOptions> Routes { get; set; } = new(StringComparer.Ordinal);

    public RateLimitOptions RateLimits { get; set; } = new();

    /// <summary>Price per million tokens by model.</summary>
    public Dictionary<string, ModelPricing> Pricing { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Optional per-model capability overrides; the Models API and built-in defaults fill the rest.</summary>
    public Dictionary<string, ModelProfileOptions> ModelProfiles { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>One task route: which provider and model serve it, with what limits.</summary>
public sealed class RouteOptions
{
    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary><c>low</c> … <c>max</c>; sent only to models whose profile supports effort.</summary>
    public string? Effort { get; set; }

    public int MaxTokens { get; set; } = 4000;

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Embedding routes: the vector dimension every result must have.</summary>
    public int? Dimensions { get; set; }
}

public sealed class RateLimitOptions
{
    /// <summary>Model calls each tenant may start per minute (research R21).</summary>
    public int PerTenantRequestsPerMinute { get; set; } = 60;
}

/// <summary>
/// List prices in USD per million tokens. Cache prices default to the provider's usual multipliers of
/// the input price (reads 0.1×, writes 1.25×) when not configured.
/// </summary>
public sealed class ModelPricing
{
    public decimal InputPerMTok { get; set; }

    public decimal OutputPerMTok { get; set; }

    public decimal? CacheReadPerMTok { get; set; }

    public decimal? CacheWritePerMTok { get; set; }
}

/// <summary>Configured capability values for a model; unset values keep the registry's defaults.</summary>
public sealed class ModelProfileOptions
{
    public int? ContextWindow { get; set; }

    public int? MaxOutputTokens { get; set; }

    public bool? SupportsImages { get; set; }

    public bool? SupportsPdf { get; set; }

    public bool? SupportsStructuredOutputs { get; set; }

    public bool? SupportsEffort { get; set; }

    public bool? AdaptiveThinking { get; set; }
}
