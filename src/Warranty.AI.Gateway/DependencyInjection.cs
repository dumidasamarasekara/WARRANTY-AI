using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warranty.AI.Gateway.Imaging;
using Warranty.AI.Gateway.Prompts;
using Warranty.AI.Gateway.Providers;
using Warranty.AI.Gateway.Providers.Anthropic;
using Warranty.AI.Gateway.Providers.Embeddings;
using Warranty.AI.Gateway.Providers.Ollama;
using Warranty.AI.Gateway.Providers.Replay;
using Warranty.AI.Gateway.RateLimiting;
using Warranty.AI.Gateway.Redaction;
using Warranty.AI.Gateway.Routing;
using Warranty.AI.Gateway.Usage;
using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Gateway;

public static class DependencyInjection
{
    /// <summary>The route whose provider and model serve <see cref="IAiGateway.EmbedAsync"/>.</summary>
    public const string EmbeddingRoute = "embedding";

    /// <summary>Connection string name of the Ollama embedding model (Aspire: <c>Endpoint=…;Model=…</c>).</summary>
    public const string EmbeddingsConnection = "embeddings";

    /// <summary>
    /// Registers <c>IAiGateway</c> with its routes, prompt templates and providers (contracts/ai-gateway.md).
    /// <c>AiGateway:Mode</c> picks the chat provider per call — <c>live</c> uses each route's provider,
    /// <c>replay</c> answers every chat route from recordings — so a host or test can switch modes by
    /// configuration alone. The section is validated when the host starts: a chat route whose provider
    /// does not declare <c>"NoTraining": true</c> stops startup (FR-006a, research R28). Routed Anthropic
    /// model capabilities are read from the Models API at startup, falling back to configured profiles.
    /// Requires a scoped <c>ITenantContext</c>, <c>IAiOpsRepository</c> and <c>IClaimRepository</c> from the host.
    /// </summary>
    public static IServiceCollection AddWarrantyAiGateway(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AiGatewayOptions>()
            .Bind(configuration.GetSection(AiGatewayOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AiGatewayOptions>, AiGatewayOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ModelProfileRegistry>();
        services.AddSingleton(_ => PromptTemplateRegistry.FromEmbeddedResources());
        services.AddSingleton<IPiiRedactor, RegexPiiRedactor>();
        services.AddSingleton<TenantRateLimiter>();
        services.AddSingleton<ImageDownscaler>();
        services.AddScoped<UsageRecorder>();

        // Anthropic: the client is created on first use, so replay hosts start without a key.
        services.TryAddSingleton<IAnthropicClient>(sp => CreateAnthropicClient(sp.GetRequiredService<IOptions<AiGatewayOptions>>().Value.Anthropic));
        services.AddSingleton<AnthropicModelProvider>();
        services.AddSingleton<IModelProvider>(sp => sp.GetRequiredService<AnthropicModelProvider>());
        services.AddHostedService<AnthropicModelProfileLoader>();

        // Ollama: self-hosted chat models (AiGateway:Ollama); nothing is called unless a route uses it.
        services.AddSingleton<PdfRasterizer>();
        services.AddSingleton<OllamaClient>();
        services.AddSingleton<OllamaModelCatalog>();
        services.AddSingleton<OllamaModelProvider>();
        services.AddSingleton<IModelProvider>(sp => sp.GetRequiredService<OllamaModelProvider>());
        services.AddHostedService<OllamaModelProfileLoader>();

        // Replay: recordings chosen by the claim's serial number; when recording, it wraps the Anthropic provider.
        services.AddSingleton<ReplayCallCounter>();
        services.AddSingleton(sp => ReplayScenarioCatalog.Load(sp.GetRequiredService<IOptions<AiGatewayOptions>>()));
        services.AddScoped<IReplayScenarioSelector, SerialNumberScenarioSelector>();
        services.AddScoped<IModelProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AiGatewayOptions>>();
            return new ReplayModelProvider(
                sp.GetRequiredService<IReplayScenarioSelector>(),
                sp.GetRequiredService<ReplayCallCounter>(),
                options,
                sp.GetRequiredService<ILogger<ReplayModelProvider>>(),
                options.Value.Replay.Record ? sp.GetRequiredService<AnthropicModelProvider>() : null);
        });

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            CreateEmbeddingGenerator(sp.GetRequiredService<IOptions<AiGatewayOptions>>().Value, configuration));

        services.AddScoped<IAiGateway, AiGateway>();
        return services;
    }

    private static AnthropicClient CreateAnthropicClient(AnthropicProviderOptions anthropic)
        => string.IsNullOrWhiteSpace(anthropic.ApiKey)
            ? new AnthropicClient { MaxRetries = anthropic.MaxRetries }
            : new AnthropicClient { ApiKey = anthropic.ApiKey, MaxRetries = anthropic.MaxRetries };

    /// <summary>The generator for the <see cref="EmbeddingRoute"/>: Ollama, or the deterministic hash generator.</summary>
    internal static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(AiGatewayOptions options, IConfiguration configuration)
    {
        if (!options.Routes.TryGetValue(EmbeddingRoute, out var route) || route.Dimensions is not { } dimensions)
        {
            throw new InvalidOperationException($"AiGateway:Routes:{EmbeddingRoute} must be configured with Dimensions.");
        }

        if (route.Provider == HashEmbeddingGenerator.ProviderName)
        {
            return new HashEmbeddingGenerator(dimensions);
        }

        var connectionString = configuration.GetConnectionString(EmbeddingsConnection);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"Connection string '{EmbeddingsConnection}' (the Ollama embedding model) is not configured.");
        }

        var ollama = OllamaEmbeddingOptions.FromConnectionString(connectionString);
        ollama.Model = route.Model;
        ollama.Dimensions = dimensions;
        return new OllamaEmbeddingProvider(new HttpClient(), ollama);
    }
}
