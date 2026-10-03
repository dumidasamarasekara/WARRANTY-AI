using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Warranty.AI.Gateway.Routing;

/// <summary>
/// What a model can do, used by providers to shape requests (contracts/ai-gateway.md): e.g. effort is
/// never sent to a model without effort support, and a model without native structured outputs gets
/// the schema in the prompt plus a strict parse instead.
/// </summary>
public sealed record ModelProfile(
    string Model,
    int ContextWindow,
    int MaxOutputTokens,
    bool SupportsImages,
    bool SupportsPdf,
    bool SupportsStructuredOutputs,
    bool SupportsEffort,
    bool AdaptiveThinking)
{
    /// <summary>A conservative profile for a model nothing is known about.</summary>
    public static ModelProfile Unknown(string model) => new(model, 200_000, 4_096, false, false, false, false, false);
}

/// <summary>A chat or embedding route resolved to its options and model profile.</summary>
public sealed record ResolvedRoute(string Name, RouteOptions Options, ModelProfile Profile);

/// <summary>
/// Model profiles by model ID and route resolution. Profiles start from built-in defaults for the
/// routed Anthropic models, are refreshed from the provider's Models API at startup (T031) and are
/// finally overridden by <c>AiGateway:ModelProfiles</c>, so configuration always wins.
/// </summary>
public sealed class ModelProfileRegistry
{
    private static readonly IReadOnlyDictionary<string, ModelProfile> BuiltIn = new Dictionary<string, ModelProfile>(StringComparer.Ordinal)
    {
        // Thinking cannot be disabled and runs adaptive; effort low…max (default medium, so routes set it).
        ["claude-opus-5-5"] = new("claude-opus-5-5", 1_000_000, 128_000, true, true, true, true, true),

        // No effort parameter and no adaptive thinking on Haiku 4.5.
        ["claude-haiku-4-5"] = new("claude-haiku-4-5", 200_000, 64_000, true, true, true, false, false),
    };

    private readonly ConcurrentDictionary<string, ModelProfile> _profiles;
    private readonly AiGatewayOptions _options;

    public ModelProfileRegistry(IOptions<AiGatewayOptions> options)
    {
        _options = options.Value;
        _profiles = new ConcurrentDictionary<string, ModelProfile>(BuiltIn, StringComparer.Ordinal);
        foreach (var (model, overrides) in _options.ModelProfiles)
        {
            _profiles[model] = Apply(Get(model), overrides);
        }
    }

    public ModelProfile Get(string model)
        => _profiles.TryGetValue(model, out var profile) ? profile : ModelProfile.Unknown(model);

    /// <summary>Records capabilities discovered from a provider; configured overrides still take precedence.</summary>
    public void Update(ModelProfile discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        _profiles[discovered.Model] = _options.ModelProfiles.TryGetValue(discovered.Model, out var overrides)
            ? Apply(discovered, overrides)
            : discovered;
    }

    /// <summary>The configured route; throws for an unknown route name (a programming error, not a model failure).</summary>
    public ResolvedRoute Resolve(string route)
    {
        if (!_options.Routes.TryGetValue(route, out var options))
        {
            throw new ArgumentException($"AI route '{route}' is not configured.", nameof(route));
        }

        return new ResolvedRoute(route, options, Get(options.Model));
    }

    private static ModelProfile Apply(ModelProfile profile, ModelProfileOptions overrides) => profile with
    {
        ContextWindow = overrides.ContextWindow ?? profile.ContextWindow,
        MaxOutputTokens = overrides.MaxOutputTokens ?? profile.MaxOutputTokens,
        SupportsImages = overrides.SupportsImages ?? profile.SupportsImages,
        SupportsPdf = overrides.SupportsPdf ?? profile.SupportsPdf,
        SupportsStructuredOutputs = overrides.SupportsStructuredOutputs ?? profile.SupportsStructuredOutputs,
        SupportsEffort = overrides.SupportsEffort ?? profile.SupportsEffort,
        AdaptiveThinking = overrides.AdaptiveThinking ?? profile.AdaptiveThinking,
    };
}
