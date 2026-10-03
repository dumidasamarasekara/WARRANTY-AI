using Anthropic;
using Anthropic.Models.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warranty.AI.Gateway.Routing;

namespace Warranty.AI.Gateway.Providers.Anthropic;

/// <summary>
/// Reads the capabilities of every routed Anthropic model from the Models API when the host starts
/// (contracts/ai-gateway.md, research R4) and records them in the <see cref="ModelProfileRegistry"/>.
/// The lookup is best effort: when the API cannot be reached, the key is missing or the time budget
/// runs out, the built-in and configured profiles stay in force and startup continues. Replay runs
/// make no provider calls, so nothing is read unless replay is recording.
/// </summary>
internal sealed class AnthropicModelProfileLoader(
    IAnthropicClient client,
    ModelProfileRegistry registry,
    IOptions<AiGatewayOptions> options,
    ILogger<AnthropicModelProfileLoader> logger) : IHostedService
{
    /// <summary>The most startup may wait for the Models API, across all routed models.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var gateway = options.Value;
        if (gateway.Mode == AiGatewayOptions.ReplayMode && !gateway.Replay.Record)
        {
            return;
        }

        var models = gateway.Routes.Values
            .Where(r => r.Dimensions is null && r.Provider == AnthropicModelProvider.ProviderName)
            .Select(r => r.Model)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        foreach (var model in models)
        {
            try
            {
                var info = await client.Models.Retrieve(model, cancellationToken: budget.Token);
                var profile = ToProfile(info, registry.Get(model));
                registry.Update(profile);
                logger.LogInformation(
                    "Model profile for {Model} loaded from the Models API: {ContextWindow} context, {MaxOutputTokens} max output, structured outputs {StructuredOutputs}, effort {Effort}.",
                    model, profile.ContextWindow, profile.MaxOutputTokens, profile.SupportsStructuredOutputs, profile.SupportsEffort);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Could not read {Model} from the Anthropic Models API ({Error}); using the built-in and configured profile.",
                    model, ex.GetType().Name);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Overlays what the Models API reports on the current profile. Adaptive-only thinking and the
    /// refusal fallback are not reported by the API, so they keep their built-in or configured values.
    /// </summary>
    internal static ModelProfile ToProfile(ModelInfo info, ModelProfile current)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(current);
        var capabilities = info.Capabilities;
        return current with
        {
            ContextWindow = info.MaxInputTokens is { } input ? ToInt(input) : current.ContextWindow,
            MaxOutputTokens = info.MaxTokens is { } output ? ToInt(output) : current.MaxOutputTokens,
            SupportsImages = capabilities?.ImageInput?.Supported ?? current.SupportsImages,
            SupportsPdf = capabilities?.PdfInput?.Supported ?? current.SupportsPdf,
            SupportsStructuredOutputs = capabilities?.StructuredOutputs?.Supported ?? current.SupportsStructuredOutputs,
            SupportsEffort = capabilities?.Effort?.Supported ?? current.SupportsEffort,
        };
    }

    private static int ToInt(long value) => (int)Math.Clamp(value, 1, int.MaxValue);
}
