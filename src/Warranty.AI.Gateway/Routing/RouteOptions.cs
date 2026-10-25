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

    public AnthropicProviderOptions Anthropic { get; set; } = new();

    public OllamaProviderOptions Ollama { get; set; } = new();

    public ReplayProviderOptions Replay { get; set; } = new();

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

    public bool? SupportsRefusalFallback { get; set; }
}

/// <summary>The <c>AiGateway:Anthropic</c> provider section (contracts/ai-gateway.md).</summary>
public sealed class AnthropicProviderOptions
{
    public const string DefaultRefusalFallback = "default";

    /// <summary>
    /// The API key, set by the AppHost from its <c>anthropic-api-key</c> parameter; when empty the SDK
    /// reads <c>ANTHROPIC_API_KEY</c>. Replay runs need no key.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// <c>default</c> lets the API re-serve a safety-classifier refusal on a fallback model chosen by
    /// refusal category; empty or <c>none</c> turns the fallback off. Sent only to models whose
    /// profile supports it.
    /// </summary>
    public string? RefusalFallback { get; set; } = DefaultRefusalFallback;

    /// <summary>SDK retries for 408/409/429/5xx and connection errors.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>The provider does not train on API inputs (FR-006a); routes to a provider without it are refused.</summary>
    public bool NoTraining { get; set; }
}

/// <summary>
/// The <c>AiGateway:Ollama</c> provider section: chat models served by a self-hosted Ollama server, for
/// local runs without a paid API. Embeddings keep their own connection string (research R5).
/// </summary>
public sealed class OllamaProviderOptions
{
    public const string DefaultEndpoint = "http://localhost:11434";

    /// <summary>The Ollama server for chat routes.</summary>
    public Uri Endpoint { get; set; } = new(DefaultEndpoint);

    /// <summary>
    /// The context window loaded per request (<c>num_ctx</c>). Ollama's own default is far smaller than
    /// a claim's prompt, and it silently drops the start of a prompt that does not fit.
    /// </summary>
    public int ContextLength { get; set; } = 12_288;

    /// <summary>How long Ollama keeps the model loaded after a call (e.g. <c>10m</c>); empty uses the server default.</summary>
    public string? KeepAlive { get; set; } = "10m";

    /// <summary>Sampling temperature; low, so the same claim gets the same answer.</summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>Sent as <c>think</c> to models that report the thinking capability; false keeps turns fast.</summary>
    public bool Think { get; set; }

    /// <summary>PDF pages rendered to images per document (Ollama models accept images, not PDFs).</summary>
    public int MaxPdfPages { get; set; } = 3;

    /// <summary>
    /// The provider does not train on inputs (FR-006a). Self-hosted models run on this machine, but the
    /// declaration is still explicit, as for every chat provider.
    /// </summary>
    public bool NoTraining { get; set; }
}

/// <summary>The <c>AiGateway:Replay</c> section: where recorded model responses live (research R18).</summary>
public sealed class ReplayProviderOptions
{
    /// <summary>Root of <c>{scenarioId}/{agent}-{callIndex}.json</c>; relative paths are searched upwards from the app directory.</summary>
    public string RecordingsPath { get; set; } = "tests/fixtures/ai-recordings";

    /// <summary>The golden scenario list that maps a claim's serial number to its scenario.</summary>
    public string ScenariosPath { get; set; } = "seed/golden/scenarios.json";

    /// <summary>Calls the live provider and writes each response as a fixture instead of reading fixtures.</summary>
    public bool Record { get; set; }
}
