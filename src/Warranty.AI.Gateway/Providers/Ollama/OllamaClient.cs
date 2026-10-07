using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Warranty.AI.Gateway.Routing;

namespace Warranty.AI.Gateway.Providers.Ollama;

/// <summary>An Ollama API reply: the HTTP status and the body as text.</summary>
internal sealed record OllamaReply(HttpStatusCode Status, string Body)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;

    /// <summary>Ollama's <c>{"error": "…"}</c> message, or the start of the body.</summary>
    public string Error
    {
        get
        {
            try
            {
                using var document = JsonDocument.Parse(Body);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("error", out var error)
                    && error.GetString() is { Length: > 0 } message)
                {
                    return message;
                }
            }
            catch (JsonException)
            {
                // Not JSON; fall through to the raw text.
            }

            return Body.Length <= 300 ? Body : Body[..300];
        }
    }
}

/// <summary>
/// The HTTP connection to the Ollama server of the chat routes (<c>AiGateway:Ollama:Endpoint</c>).
/// Calls have no client-side timeout: the gateway cancels each turn at its route's timeout.
/// Connection failures surface as <see cref="HttpRequestException"/>.
/// </summary>
internal sealed class OllamaClient
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;

    public OllamaClient(IOptions<AiGatewayOptions> options)
        : this(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, options)
    {
    }

    internal OllamaClient(HttpClient http, IOptions<AiGatewayOptions> options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _endpoint = options.Value.Ollama.Endpoint;
    }

    public Uri Endpoint => _endpoint;

    public async Task<OllamaReply> PostAsync(string path, JsonObject body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(new Uri(_endpoint, path), content, ct);
        return new OllamaReply(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }
}
