using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warranty.AI.Gateway.Routing;

namespace Warranty.AI.Gateway.Providers.Ollama;

/// <summary>
/// Reads every routed Ollama chat model from the server when the host starts and records its profile
/// in the <see cref="ModelProfileRegistry"/>: images (and so rendered PDFs) when the model has the
/// vision capability, structured outputs always, the configured context length as both context and
/// output limit. It warns about what would fail later — a model that is not pulled, a model without
/// vision on a route that is sent evidence images, a model without tool calling. Best effort, like
/// the Anthropic loader: startup never waits more than <see cref="Budget"/> or fails because of it.
/// </summary>
internal sealed class OllamaModelProfileLoader(
    OllamaModelCatalog catalog,
    ModelProfileRegistry registry,
    IOptions<AiGatewayOptions> options,
    ILogger<OllamaModelProfileLoader> logger) : IHostedService
{
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>Routes whose prompts attach evidence images or PDFs.</summary>
    private static readonly HashSet<string> ImageRoutes = new(["extraction", "vision"], StringComparer.Ordinal);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var gateway = options.Value;
        if (gateway.Mode == AiGatewayOptions.ReplayMode)
        {
            return;
        }

        var routes = gateway.Routes
            .Where(r => r.Value.Dimensions is null && r.Value.Provider == OllamaModelProvider.ProviderName)
            .ToList();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        foreach (var model in routes.Select(r => r.Value.Model).Distinct(StringComparer.Ordinal))
        {
            var routeNames = routes.Where(r => r.Value.Model == model).Select(r => r.Key).ToList();
            try
            {
                var (info, problem) = await catalog.GetAsync(model, budget.Token);
                if (info is null)
                {
                    logger.LogWarning("{Problem} Routes {Routes} will fail until it is pulled.", problem, routeNames);
                    continue;
                }

                registry.Update(ToProfile(info, gateway.Ollama));
                logger.LogInformation(
                    "Ollama model {Model} for routes {Routes}: capabilities {Capabilities}, context {ContextLength}.",
                    model, routeNames, info.Capabilities.Order(StringComparer.Ordinal), gateway.Ollama.ContextLength);

                if (!info.Vision && routeNames.Any(ImageRoutes.Contains))
                {
                    logger.LogWarning(
                        "Ollama model {Model} cannot read images, but routes {Routes} are sent invoice and photo evidence; those steps will fail and claims go to human review. Use a vision model.",
                        model, routeNames.Where(ImageRoutes.Contains));
                }

                if (!info.Tools)
                {
                    logger.LogWarning("Ollama model {Model} does not support tool calling; agent steps that offer tools will fail.", model);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Could not read {Model} from Ollama at {Endpoint} ({Error}); is Ollama running? Using the configured profile.",
                    model, gateway.Ollama.Endpoint, ex.GetType().Name);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static ModelProfile ToProfile(OllamaModelInfo info, OllamaProviderOptions ollama)
    {
        var context = Math.Min(ollama.ContextLength, info.ContextLength ?? int.MaxValue);
        return new ModelProfile(
            info.Model,
            context,
            context,
            SupportsImages: info.Vision,
            SupportsPdf: info.Vision,
            SupportsStructuredOutputs: true,
            SupportsEffort: false,
            AdaptiveThinking: false);
    }
}
