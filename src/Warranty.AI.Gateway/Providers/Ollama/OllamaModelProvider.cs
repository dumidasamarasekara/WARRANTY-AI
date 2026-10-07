using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warranty.AI.Gateway.Imaging;
using Warranty.AI.Gateway.Routing;
using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Gateway.Providers.Ollama;

/// <summary>
/// The <c>ollama</c> chat provider: maps a resolved turn onto Ollama's <c>/api/chat</c> for a model
/// running on a self-hosted server, so the PoC can run without a paid API. Images are downscaled to
/// JPEG and PDFs are rendered to page images (Ollama models do not read PDFs); attachments are
/// listed by evidence reference because Ollama attaches a message's images after its text. Tools are
/// offered as functions; the output schema always goes in the system prompt and, on turns without
/// tools, also as Ollama's <c>format</c> so decoding is constrained to it (constraining a turn that may
/// call a tool would stop the tool call). Replies are parsed leniently — code fences and stray prose
/// around the JSON value are dropped — and the gateway still validates them against the schema.
/// </summary>
internal sealed partial class OllamaModelProvider(
    OllamaClient client,
    OllamaModelCatalog catalog,
    ImageDownscaler downscaler,
    PdfRasterizer pdfs,
    IOptions<AiGatewayOptions> options,
    ILogger<OllamaModelProvider> logger) : IModelProvider
{
    public const string ProviderName = "ollama";

    public string Name => ProviderName;

    public async Task<AiTurnResult> CompleteAsync(ResolvedTurnRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = request.Route.Options.Model;

        try
        {
            var (info, problem) = await catalog.GetAsync(model, ct);
            if (info is null)
            {
                return Failure(model, AiFailureKind.ProviderError, problem!);
            }

            JsonObject body;
            try
            {
                body = BuildRequest(request, info);
            }
            catch (UnsupportedContentException ex)
            {
                return Failure(model, AiFailureKind.ProviderError, ex.Message);
            }

            var reply = await client.PostAsync("api/chat", body, ct);
            if (!reply.IsSuccess)
            {
                logger.LogWarning("Ollama rejected the {Route} request for {Model}: {Status} {Error}.", request.Route.Name, model, (int)reply.Status, reply.Error);
                return Failure(model, KindOf(reply.Status), $"Ollama returned {(int)reply.Status}: {reply.Error}");
            }

            using var document = JsonDocument.Parse(reply.Body);
            return MapResponse(request, document.RootElement);
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            return Failure(model, AiFailureKind.Transient, $"Cannot reach Ollama at {client.Endpoint}: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return Failure(model, AiFailureKind.ProviderError, $"Ollama returned a reply that is not JSON: {ex.Message}");
        }
    }

    /// <summary>Builds the <c>/api/chat</c> request for a turn.</summary>
    internal JsonObject BuildRequest(ResolvedTurnRequest request, OllamaModelInfo info)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(info);
        var ollama = options.Value.Ollama;
        var schema = request.Request.OutputSchema;
        var acceptsImages = info.Vision || request.Route.Profile.SupportsImages;

        var system = schema is null ? request.SystemPrompt : request.SystemPrompt + SchemaInstruction(schema);
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in request.Messages)
        {
            if (message.Role == AiRole.Assistant)
            {
                messages.Add(Assistant(message, toolNames));
                continue;
            }

            foreach (var mapped in User(message, info.Model, acceptsImages, toolNames))
            {
                messages.Add(mapped);
            }
        }

        var body = new JsonObject
        {
            ["model"] = request.Route.Options.Model,
            ["messages"] = messages,
            ["stream"] = false,
            ["options"] = new JsonObject
            {
                ["num_ctx"] = ollama.ContextLength,
                ["num_predict"] = request.MaxTokens,
                ["temperature"] = ollama.Temperature,
            },
        };

        if (request.Request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Request.Tools.Select(ToTool).ToArray<JsonNode?>());
        }
        else if (schema is not null && request.Route.Profile.SupportsStructuredOutputs)
        {
            body["format"] = JsonNode.Parse(schema.Schema.GetRawText());
        }

        if (info.Thinking)
        {
            body["think"] = ollama.Think;
        }

        if (!string.IsNullOrWhiteSpace(ollama.KeepAlive))
        {
            body["keep_alive"] = ollama.KeepAlive;
        }

        return body;
    }

    /// <summary>Maps a <c>/api/chat</c> reply onto a provider-neutral turn result with raw usage.</summary>
    internal AiTurnResult MapResponse(ResolvedTurnRequest request, JsonElement response)
    {
        ArgumentNullException.ThrowIfNull(request);
        var served = response.TryGetProperty("model", out var m) && m.GetString() is { Length: > 0 } name ? name : request.Route.Options.Model;
        var usage = new AiUsage(ProviderName, served, Count(response, "prompt_eval_count"), Count(response, "eval_count"), 0, 0, TimeSpan.Zero, 0m);

        var parts = new List<AiContentPart>();
        var toolCalls = new List<AiToolCall>();
        var text = string.Empty;
        if (response.TryGetProperty("message", out var message))
        {
            text = ThinkBlock().Replace(message.TryGetProperty("content", out var content) ? content.GetString() ?? string.Empty : string.Empty, string.Empty).Trim();
            if (text.Length > 0)
            {
                parts.Add(new TextPart(text));
            }

            if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in calls.EnumerateArray())
                {
                    var toolCall = ToToolCall(call, toolCalls.Count);
                    parts.Add(new ToolCallPart(toolCall.CallId, toolCall.ToolName, toolCall.Arguments));
                    toolCalls.Add(toolCall);
                }
            }
        }

        var assistant = new AiMessage(AiRole.Assistant, parts);
        var doneReason = response.TryGetProperty("done_reason", out var reason) ? reason.GetString() : null;
        return doneReason switch
        {
            "length" => new AiTurnResult(AiStopKind.Truncated, assistant, [], null, usage, null),
            _ when toolCalls.Count > 0 => new AiTurnResult(AiStopKind.ToolCalls, assistant, toolCalls, null, usage, null),
            "stop" or null => Completed(request, assistant, usage, text),
            _ => new AiTurnResult(
                AiStopKind.Failed, assistant, [], null, usage,
                new AiFailure(AiFailureKind.ProviderError, $"Unexpected done reason '{doneReason}'.")),
        };
    }

    /// <summary>A finished turn; when a schema was requested the reply must contain one JSON value.</summary>
    private static AiTurnResult Completed(ResolvedTurnRequest request, AiMessage message, AiUsage usage, string text)
    {
        if (request.Request.OutputSchema is null)
        {
            return new AiTurnResult(AiStopKind.Completed, message, [], null, usage, null);
        }

        if (ExtractJson(text) is { } json)
        {
            return new AiTurnResult(AiStopKind.Completed, message, [], json, usage, null);
        }

        return new AiTurnResult(
            AiStopKind.Failed, message, [], null, usage,
            new AiFailure(AiFailureKind.InvalidOutput, "The model reply contains no JSON value.", [text.Length <= 200 ? text : text[..200]]));
    }

    /// <summary>The reply as JSON: as is, without a code fence, or the outermost object inside surrounding prose.</summary>
    internal static JsonElement? ExtractJson(string text)
    {
        var candidates = new List<string> { text.Trim() };
        if (CodeFence().Match(text) is { Success: true } fence)
        {
            candidates.Add(fence.Groups["json"].Value.Trim());
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            candidates.Add(text[start..(end + 1)]);
        }

        foreach (var candidate in candidates.Where(c => c.Length > 0))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    private static JsonObject Assistant(AiMessage message, Dictionary<string, string> toolNames)
    {
        var text = new StringBuilder();
        var calls = new JsonArray();
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case TextPart t:
                    AppendParagraph(text, t.Text);
                    break;
                case ToolCallPart call:
                    toolNames[call.CallId] = call.ToolName;
                    calls.Add(new JsonObject
                    {
                        ["id"] = call.CallId,
                        ["function"] = new JsonObject { ["name"] = call.ToolName, ["arguments"] = JsonNode.Parse(call.Arguments.GetRawText()) },
                    });
                    break;
                default:
                    // Opaque blocks of other providers mean nothing to Ollama and are not sent.
                    break;
            }
        }

        var mapped = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString() };
        if (calls.Count > 0)
        {
            mapped["tool_calls"] = calls;
        }

        return mapped;
    }

    /// <summary>
    /// A user message: tool results become <c>tool</c> messages (first, so they follow the assistant's
    /// calls), the rest one <c>user</c> message whose images are listed by reference at its end.
    /// </summary>
    private List<JsonObject> User(AiMessage message, string model, bool acceptsImages, Dictionary<string, string> toolNames)
    {
        var mapped = new List<JsonObject>();
        var text = new StringBuilder();
        var images = new JsonArray();
        var imageRefs = new List<string>();
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case TextPart t:
                    AppendParagraph(text, t.Text);
                    break;
                case UntrustedTextPart untrusted:
                    AppendParagraph(text, UntrustedDelimiter.Wrap(untrusted));
                    break;
                case ImagePart image:
                    RequireImages(acceptsImages, model, image.EvidenceRef);
                    images.Add(Convert.ToBase64String(Downscale(image).Data));
                    imageRefs.Add(image.EvidenceRef);
                    break;
                case DocumentPart document:
                    RequireImages(acceptsImages, model, document.EvidenceRef);
                    var (pages, pageCount) = Render(document);
                    for (var page = 0; page < pages.Count; page++)
                    {
                        images.Add(Convert.ToBase64String(pages[page].Data));
                        imageRefs.Add(string.Create(CultureInfo.InvariantCulture, $"{document.EvidenceRef} page {page + 1} of {pageCount}"));
                    }

                    break;
                case ToolResultPart result:
                    mapped.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_name"] = toolNames.TryGetValue(result.CallId, out var tool) ? tool : string.Empty,
                        ["content"] = result.IsError ? $"Tool error: {result.Result.GetRawText()}" : result.Result.GetRawText(),
                    });
                    break;
                case ProviderOpaquePart:
                    break;
                default:
                    throw new UnsupportedContentException($"Content part {part.GetType().Name} is not supported.");
            }
        }

        if (imageRefs.Count > 0)
        {
            AppendParagraph(text, "Images attached to this message, in order: "
                                  + string.Join("; ", imageRefs.Select((r, i) => string.Create(CultureInfo.InvariantCulture, $"image {i + 1} = {r}"))) + ".");
        }

        if (text.Length > 0 || images.Count > 0)
        {
            var user = new JsonObject { ["role"] = "user", ["content"] = text.ToString() };
            if (images.Count > 0)
            {
                user["images"] = images;
            }

            mapped.Add(user);
        }

        return mapped;
    }

    private DownscaledImage Downscale(ImagePart image)
    {
        try
        {
            return downscaler.Downscale(image.Data.Span);
        }
        catch (InvalidDataException ex)
        {
            throw new UnsupportedContentException($"Evidence image {image.EvidenceRef} cannot be sent: {ex.Message}");
        }
    }

    private (IReadOnlyList<DownscaledImage> Pages, int PageCount) Render(DocumentPart document)
    {
        try
        {
            return pdfs.Render(document.PdfData, options.Value.Ollama.MaxPdfPages);
        }
        catch (InvalidDataException ex)
        {
            throw new UnsupportedContentException($"Evidence document {document.EvidenceRef} cannot be sent: {ex.Message}");
        }
    }

    private static void RequireImages(bool acceptsImages, string model, string evidenceRef)
    {
        if (!acceptsImages)
        {
            throw new UnsupportedContentException(
                $"Ollama model {model} does not accept images, so {evidenceRef} cannot be sent; route this step to a vision model.");
        }
    }

    private static AiToolCall ToToolCall(JsonElement call, int index)
    {
        var function = call.GetProperty("function");
        var name = function.GetProperty("name").GetString() ?? string.Empty;
        var arguments = function.TryGetProperty("arguments", out var args) ? args : default;
        if (arguments.ValueKind == JsonValueKind.String)
        {
            // Some models return the arguments as a JSON string rather than an object.
            using var parsed = JsonDocument.Parse(arguments.GetString()!);
            arguments = parsed.RootElement.Clone();
        }
        else if (arguments.ValueKind == JsonValueKind.Undefined)
        {
            using var empty = JsonDocument.Parse("{}");
            arguments = empty.RootElement.Clone();
        }
        else
        {
            arguments = arguments.Clone();
        }

        var id = call.TryGetProperty("id", out var callId) && callId.GetString() is { Length: > 0 } given
            ? given
            : string.Create(CultureInfo.InvariantCulture, $"call_{index}_{Guid.NewGuid():N}");
        return new AiToolCall(id, name, arguments);
    }

    private static JsonObject ToTool(AiToolDefinition tool) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["parameters"] = JsonNode.Parse(tool.InputSchema.GetRawText()),
        },
    };

    private static string SchemaInstruction(AiOutputSchema schema)
        => "\n\nWhen you give your final answer, reply with only one JSON value, without code fences or any other text, that validates against this JSON Schema ("
           + schema.SchemaId + "):\n" + schema.Schema.GetRawText();

    private static void AppendParagraph(StringBuilder text, string paragraph)
    {
        if (text.Length > 0)
        {
            text.Append("\n\n");
        }

        text.Append(paragraph);
    }

    private static int Count(JsonElement response, string property)
        => response.TryGetProperty(property, out var value) && value.TryGetInt64(out var count) ? (int)Math.Clamp(count, 0, int.MaxValue) : 0;

    private static AiFailureKind KindOf(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => AiFailureKind.RateLimited,
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => AiFailureKind.Transient,
        _ => AiFailureKind.ProviderError,
    };

    private static AiTurnResult Failure(string model, AiFailureKind kind, string message)
        => new(AiStopKind.Failed, new AiMessage(AiRole.Assistant, []), [], null, AiUsage.None(ProviderName, model), new AiFailure(kind, message));

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"```(?:json)?\s*(?<json>.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CodeFence();

    private sealed class UnsupportedContentException(string message) : Exception(message);
}
