using Microsoft.Extensions.Options;
using Warranty.AI.Gateway.Providers.Anthropic;
using Warranty.AI.Gateway.Providers.Embeddings;
using Warranty.AI.Gateway.Providers.Replay;

namespace Warranty.AI.Gateway.Routing;

/// <summary>
/// Checks the <c>AiGateway</c> section when the host starts, so a bad configuration stops the app
/// instead of failing claims later. Above all, a chat route is refused unless its provider section
/// declares <c>"NoTraining": true</c> (FR-006a, research R28): case content may only go to a provider
/// that does not train on it. Embedding routes run on a local model and need no such declaration.
/// </summary>
internal sealed class AiGatewayOptionsValidator : IValidateOptions<AiGatewayOptions>
{
    private static readonly HashSet<string> EmbeddingProviders =
        new([OllamaEmbeddingProvider.ProviderName, HashEmbeddingGenerator.ProviderName], StringComparer.Ordinal);

    private static readonly HashSet<string> Efforts = new(["low", "medium", "high", "xhigh", "max"], StringComparer.OrdinalIgnoreCase);

    public ValidateOptionsResult Validate(string? name, AiGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.Mode is not (AiGatewayOptions.LiveMode or AiGatewayOptions.ReplayMode))
        {
            failures.Add($"AiGateway:Mode '{options.Mode}' is not '{AiGatewayOptions.LiveMode}' or '{AiGatewayOptions.ReplayMode}'.");
        }

        if (options.Routes.Count == 0)
        {
            failures.Add("AiGateway:Routes configures no routes.");
        }

        foreach (var (route, settings) in options.Routes)
        {
            if (string.IsNullOrWhiteSpace(settings.Provider) || string.IsNullOrWhiteSpace(settings.Model))
            {
                failures.Add($"Route '{route}' needs a Provider and a Model.");
                continue;
            }

            if (settings.Dimensions is { } dimensions)
            {
                if (dimensions < 1)
                {
                    failures.Add($"Embedding route '{route}' needs positive Dimensions.");
                }

                if (!EmbeddingProviders.Contains(settings.Provider))
                {
                    failures.Add($"Embedding route '{route}' uses unknown provider '{settings.Provider}'.");
                }

                continue;
            }

            if (!DeclaresNoTraining(options, settings.Provider))
            {
                failures.Add(
                    $"Route '{route}' is refused: chat provider '{settings.Provider}' has no AiGateway provider section declaring \"NoTraining\": true (FR-006a).");
            }

            if (settings.Effort is { } effort && !Efforts.Contains(effort))
            {
                failures.Add($"Route '{route}' has effort '{effort}'; use low, medium, high, xhigh or max.");
            }

            if (settings.MaxTokens < 1 || settings.TimeoutSeconds < 1)
            {
                failures.Add($"Route '{route}' needs positive MaxTokens and TimeoutSeconds.");
            }
        }

        if (options.RateLimits.PerTenantRequestsPerMinute < 1)
        {
            failures.Add("AiGateway:RateLimits:PerTenantRequestsPerMinute must be positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Whether the provider's own section promises not to train on API inputs; unknown providers never do.</summary>
    internal static bool DeclaresNoTraining(AiGatewayOptions options, string provider) => provider switch
    {
        AnthropicModelProvider.ProviderName => options.Anthropic.NoTraining,
        _ => false,
    };
}
