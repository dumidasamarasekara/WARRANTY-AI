using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warranty.AI.Gateway.Imaging;
using Warranty.AI.Gateway.Routing;
using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Gateway.Providers.Anthropic;

/// <summary>
/// The <c>anthropic</c> provider (contracts/ai-gateway.md, research R3/R4): the only code in the
/// solution that uses the official <c>Anthropic</c> SDK. It maps a resolved turn onto the beta
/// Messages API and shapes the request from the route's model profile — structured outputs when the
/// model has them (otherwise the schema goes in the system prompt and the reply is parsed strictly),
/// effort only where supported, adaptive thinking where it is always on, strict tools with
/// <c>tool_choice: auto</c> (never forced) and the server-side refusal fallback. Platform content
/// (tools, system prompt) sits before the cache breakpoint; case content follows in the messages.
/// </summary>
internal sealed partial class AnthropicModelProvider(
    IAnthropicClient client,
    ImageDownscaler downscaler,
    IOptions<AiGatewayOptions> options,
    ILogger<AnthropicModelProvider> logger) : IModelProvider
{
    public const string ProviderName = "anthropic";

    private const string UntrustedTag = "untrusted_claim_content";

    public string Name => ProviderName;

    public async Task<AiTurnResult> CompleteAsync(ResolvedTurnRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = request.Route.Options.Model;

        MessageCreateParams parameters;
        try
        {
            parameters = BuildParams(request);
        }
        catch (UnsupportedContentException ex)
        {
            return Failure(model, AiFailureKind.ProviderError, ex.Message);
        }

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(parameters, ct);
        }
        catch (AnthropicRateLimitException ex)
        {
            return Failure(model, AiFailureKind.RateLimited, Describe(ex));
        }
        catch (Anthropic5xxException ex)
        {
            return Failure(model, AiFailureKind.Transient, Describe(ex));
        }
        catch (Anthropic4xxException ex)
        {
            logger.LogWarning("Anthropic rejected the {Route} request: {Status} {ErrorType}.", request.Route.Name, (int)ex.StatusCode, ex.ErrorType);
            return Failure(model, AiFailureKind.ProviderError, Describe(ex));
        }
        catch (AnthropicApiException ex)
        {
            return Failure(model, AiFailureKind.ProviderError, Describe(ex));
        }
        catch (AnthropicIOException ex) when (!ct.IsCancellationRequested)
        {
            return Failure(model, AiFailureKind.Transient, $"Connection to Anthropic failed: {ex.Message}");
        }
        catch (AnthropicException ex) when (!ct.IsCancellationRequested)
        {
            return Failure(model, AiFailureKind.ProviderError, $"{ex.GetType().Name}: {ex.Message}");
        }

        return MapResponse(request, response);
    }

    /// <summary>Builds the beta Messages API request for a turn.</summary>
    internal MessageCreateParams BuildParams(ResolvedTurnRequest request)
    {
        var profile = request.Route.Profile;
        var schema = request.Request.OutputSchema;
        var nativeSchema = schema is not null && profile.SupportsStructuredOutputs;

        var systemPrompt = schema is not null && !nativeSchema
            ? request.SystemPrompt + SchemaInstruction(schema)
            : request.SystemPrompt;

        var outputConfig = new BetaOutputConfig();
        var hasOutputConfig = false;
        if (nativeSchema)
        {
            outputConfig = outputConfig with { Format = new BetaJsonOutputFormat { Schema = ToDictionary(schema!.Schema) } };
            hasOutputConfig = true;
        }

        if (profile.SupportsEffort && request.Route.Options.Effort is { } effort)
        {
            outputConfig = outputConfig with { Effort = ParseEffort(effort) };
            hasOutputConfig = true;
        }

        var fallback = options.Value.Anthropic.RefusalFallback;
        var useFallback = profile.SupportsRefusalFallback
                          && string.Equals(fallback, AnthropicProviderOptions.DefaultRefusalFallback, StringComparison.OrdinalIgnoreCase);

        var tools = request.Request.Tools.Select(ToTool).ToList();

        // Optional fields are only set when used, so the request never carries explicit nulls.
        var parameters = new MessageCreateParams
        {
            Model = request.Route.Options.Model,
            MaxTokens = request.MaxTokens,
            System = new List<BetaTextBlockParam>
            {
                // Everything up to here (tools, then system) is platform content and cacheable.
                new() { Text = systemPrompt, CacheControl = new BetaCacheControlEphemeral() },
            },
            Messages = request.Messages
                .Select(m => ToMessage(m, profile))
                .Where(m => m is not null)
                .Select(m => m!)
                .ToList(),
        };

        if (tools.Count > 0)
        {
            parameters = parameters with { Tools = tools, ToolChoice = new BetaToolChoiceAuto() };
        }

        if (hasOutputConfig)
        {
            parameters = parameters with { OutputConfig = outputConfig };
        }

        if (profile.AdaptiveThinking)
        {
            parameters = parameters with { Thinking = new BetaThinkingConfigAdaptive() };
        }

        if (useFallback)
        {
            parameters = parameters with { Fallbacks = new Default(), Betas = [AnthropicBeta.ServerSideFallback2026_07_01] };
        }

        return parameters;
    }

    /// <summary>Maps the API response onto a provider-neutral turn result with raw usage.</summary>
    internal AiTurnResult MapResponse(ResolvedTurnRequest request, BetaMessage response)
    {
        var parts = new List<AiContentPart>();
        var toolCalls = new List<AiToolCall>();
        var text = new StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var textBlock))
            {
                parts.Add(new TextPart(textBlock.Text));
                text.Append(textBlock.Text);
            }
            else if (block.TryPickToolUse(out var toolUse))
            {
                var arguments = JsonSerializer.SerializeToElement(toolUse.Input);
                parts.Add(new ToolCallPart(toolUse.ID, toolUse.Name, arguments));
                toolCalls.Add(new AiToolCall(toolUse.ID, toolUse.Name, arguments));
            }
            else
            {
                // Thinking, redacted thinking and fallback blocks go back exactly as received.
                parts.Add(new ProviderOpaquePart(ProviderName, block.Json.Clone()));
            }
        }

        var usage = response.Usage;
        var aiUsage = new AiUsage(
            ProviderName,
            response.Model.Raw() is { Length: > 0 } served ? served : request.Route.Options.Model,
            ToInt(usage.InputTokens),
            ToInt(usage.OutputTokens),
            ToInt(usage.CacheReadInputTokens ?? 0),
            ToInt(usage.CacheCreationInputTokens ?? 0),
            TimeSpan.Zero,
            0m);
        var message = new AiMessage(AiRole.Assistant, parts);

        var stopReason = response.StopReason?.Raw();
        switch (stopReason)
        {
            case "end_turn" or "stop_sequence":
                return Completed(request, message, aiUsage, text.ToString());
            case "tool_use" when toolCalls.Count > 0:
                return new AiTurnResult(AiStopKind.ToolCalls, message, toolCalls, null, aiUsage, null);
            case "max_tokens":
                return new AiTurnResult(AiStopKind.Truncated, message, [], null, aiUsage, null);
            case "refusal":
                logger.LogWarning(
                    "Anthropic refused the {Route} turn for {Agent} (category {Category}).",
                    request.Route.Name, request.Request.Context.Agent, response.StopDetails?.Category?.Raw() ?? "none");
                return new AiTurnResult(AiStopKind.Refused, message, [], null, aiUsage, null);
            default:
                return new AiTurnResult(
                    AiStopKind.Failed, message, [], null, aiUsage,
                    new AiFailure(AiFailureKind.ProviderError, $"Unexpected stop reason '{stopReason ?? "none"}'."));
        }
    }

    /// <summary>A finished turn; when a schema was requested the reply text must be exactly one JSON value.</summary>
    private static AiTurnResult Completed(ResolvedTurnRequest request, AiMessage message, AiUsage usage, string text)
    {
        if (request.Request.OutputSchema is null)
        {
            return new AiTurnResult(AiStopKind.Completed, message, [], null, usage, null);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return new AiTurnResult(AiStopKind.Completed, message, [], document.RootElement.Clone(), usage, null);
        }
        catch (JsonException ex)
        {
            return new AiTurnResult(
                AiStopKind.Failed, message, [], null, usage,
                new AiFailure(AiFailureKind.InvalidOutput, "The model reply is not valid JSON.", [ex.Message]));
        }
    }

    private BetaMessageParam? ToMessage(AiMessage message, ModelProfile profile)
    {
        var blocks = new List<BetaContentBlockParam>();
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case TextPart text:
                    blocks.Add(new BetaTextBlockParam { Text = text.Text });
                    break;
                case UntrustedTextPart untrusted:
                    blocks.Add(new BetaTextBlockParam { Text = WrapUntrusted(untrusted) });
                    break;
                case ImagePart image:
                    if (!profile.SupportsImages)
                    {
                        throw new UnsupportedContentException($"Model {profile.Model} does not accept images.");
                    }

                    blocks.Add(ToImage(image));
                    break;
                case DocumentPart document:
                    if (!profile.SupportsPdf)
                    {
                        throw new UnsupportedContentException($"Model {profile.Model} does not accept PDF documents.");
                    }

                    blocks.Add(new BetaRequestDocumentBlock
                    {
                        Source = new BetaBase64PdfSource { Data = Convert.ToBase64String(document.PdfData.Span) },
                        Title = document.EvidenceRef,
                    });
                    break;
                case ToolCallPart call:
                    blocks.Add(new BetaToolUseBlockParam { ID = call.CallId, Name = call.ToolName, Input = ToDictionary(call.Arguments) });
                    break;
                case ToolResultPart result:
                    blocks.Add(new BetaToolResultBlockParam
                    {
                        ToolUseID = result.CallId,
                        Content = result.Result.GetRawText(),
                        IsError = result.IsError ? true : null,
                    });
                    break;
                case ProviderOpaquePart { Provider: ProviderName } opaque:
                    blocks.Add(FromOpaque(opaque.Payload));
                    break;
                case ProviderOpaquePart:
                    // Another provider's block means nothing here and is not sent.
                    break;
                default:
                    throw new UnsupportedContentException($"Content part {part.GetType().Name} is not supported.");
            }
        }

        if (blocks.Count == 0)
        {
            return null;
        }

        return new BetaMessageParam { Role = message.Role == AiRole.User ? Role.User : Role.Assistant, Content = blocks };
    }

    private BetaImageBlockParam ToImage(ImagePart image)
    {
        DownscaledImage scaled;
        try
        {
            scaled = downscaler.Downscale(image.Data.Span);
        }
        catch (InvalidDataException ex)
        {
            throw new UnsupportedContentException($"Evidence image {image.EvidenceRef} cannot be sent: {ex.Message}");
        }

        return new BetaImageBlockParam
        {
            Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(scaled.Data), MediaType = MediaType.ImageJpeg },
        };
    }

    /// <summary>Rebuilds an echoed Anthropic block; signed thinking stays byte-for-byte what the API returned.</summary>
    private static BetaContentBlockParam FromOpaque(JsonElement payload)
    {
        var raw = ToDictionary(payload);
        var type = payload.TryGetProperty("type", out var t) ? t.GetString() : null;
        return type switch
        {
            "thinking" => BetaThinkingBlockParam.FromRawUnchecked(raw),
            "redacted_thinking" => BetaRedactedThinkingBlockParam.FromRawUnchecked(raw),
            "fallback" => BetaFallbackBlockParam.FromRawUnchecked(raw),
            _ => new BetaContentBlockParam(payload),
        };
    }

    private static BetaToolUnion ToTool(AiToolDefinition tool) => new BetaTool
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchema = InputSchema.FromRawUnchecked(ToDictionary(tool.InputSchema)),
        Strict = true,
    };

    /// <summary>
    /// Claimant text goes inside a labelled delimiter; any delimiter-like tag inside the text is
    /// neutralised so the content cannot close the block and pose as instructions.
    /// </summary>
    internal static string WrapUntrusted(UntrustedTextPart part)
    {
        var label = WebUtility.HtmlEncode(part.Label);
        var body = UntrustedTagPattern().Replace(part.Text, match => WebUtility.HtmlEncode(match.Value));
        return $"<{UntrustedTag} label=\"{label}\">\n{body}\n</{UntrustedTag}>";
    }

    private static string SchemaInstruction(AiOutputSchema schema)
        => "\n\nReply with only one JSON value, without code fences or any other text, that validates against this JSON Schema ("
           + schema.SchemaId + "):\n" + schema.Schema.GetRawText();

    private static global::Anthropic.Core.ApiEnum<string, Effort> ParseEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => throw new InvalidOperationException($"Effort '{effort}' is not one of low, medium, high, xhigh, max."),
    };

    private static Dictionary<string, JsonElement> ToDictionary(JsonElement element)
        => element.Deserialize<Dictionary<string, JsonElement>>()
           ?? throw new InvalidOperationException("Expected a JSON object.");

    private static int ToInt(long value) => (int)Math.Min(value, int.MaxValue);

    private static string Describe(AnthropicApiException ex)
        => $"Anthropic returned {(int)ex.StatusCode}{(ex.ErrorType is { } type ? $" ({type})" : string.Empty)}.";

    private static AiTurnResult Failure(string model, AiFailureKind kind, string message)
        => new(AiStopKind.Failed, new AiMessage(AiRole.Assistant, []), [], null, AiUsage.None(ProviderName, model), new AiFailure(kind, message));

    [GeneratedRegex($"</?\\s*{UntrustedTag}", RegexOptions.IgnoreCase)]
    private static partial Regex UntrustedTagPattern();

    private sealed class UnsupportedContentException(string message) : Exception(message);
}
