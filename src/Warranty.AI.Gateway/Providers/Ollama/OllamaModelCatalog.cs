using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Warranty.AI.Gateway.Providers.Ollama;

/// <summary>What Ollama reports about a local model (<c>/api/show</c>).</summary>
internal sealed record OllamaModelInfo(string Model, IReadOnlySet<string> Capabilities, int? ContextLength)
{
    public bool Vision => Capabilities.Contains("vision");

    public bool Tools => Capabilities.Contains("tools");

    public bool Thinking => Capabilities.Contains("thinking");
}

/// <summary>
/// Model capabilities read from the Ollama server once per model and cached. Only answers are cached:
/// when the server cannot be reached or does not have the model, the next call asks again, so a model
/// pulled after startup is picked up without a restart.
/// </summary>
internal sealed class OllamaModelCatalog(OllamaClient client)
{
    private readonly ConcurrentDictionary<string, OllamaModelInfo> _models = new(StringComparer.Ordinal);

    /// <summary>
    /// The model's capabilities, or null with the reason when the server does not have the model.
    /// Throws <see cref="HttpRequestException"/> when the server cannot be reached.
    /// </summary>
    public async Task<(OllamaModelInfo? Info, string? Problem)> GetAsync(string model, CancellationToken ct)
    {
        if (_models.TryGetValue(model, out var known))
        {
            return (known, null);
        }

        var reply = await client.PostAsync("api/show", new JsonObject { ["model"] = model }, ct);
        if (!reply.IsSuccess)
        {
            return (null, $"Ollama at {client.Endpoint} has no model '{model}' ({reply.Error}); run 'ollama pull {model}'.");
        }

        var info = Parse(model, reply.Body);
        _models[model] = info;
        return (info, null);
    }

    internal static OllamaModelInfo Parse(string model, string showResponse)
    {
        using var document = JsonDocument.Parse(showResponse);
        var root = document.RootElement;
        var capabilities = root.TryGetProperty("capabilities", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(c => c.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];

        int? contextLength = null;
        if (root.TryGetProperty("model_info", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in details.EnumerateObject())
            {
                if (property.Name.EndsWith(".context_length", StringComparison.Ordinal) && property.Value.TryGetInt32(out var length))
                {
                    contextLength = length;
                }
            }
        }

        return new OllamaModelInfo(model, capabilities, contextLength);
    }
}
