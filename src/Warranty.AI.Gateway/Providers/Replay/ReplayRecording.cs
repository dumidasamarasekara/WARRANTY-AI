using System.Text.Json;
using System.Text.Json.Serialization;
using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Gateway.Providers.Replay;

/// <summary>
/// One recorded model turn: <c>tests/fixtures/ai-recordings/{scenarioId}/{agent}-{callIndex}.json</c>.
/// It holds what the harness consumes from an <see cref="AiTurnResult"/> — stop kind, tool calls,
/// structured output, usage and an optional failure — so fixtures can be written by hand. Provider
/// blocks such as signed thinking are never recorded.
/// </summary>
public sealed record ReplayRecording
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public AiStopKind Stop { get; init; }

    /// <summary>Free assistant text; a structured output is replayed as its JSON text instead.</summary>
    public string? Text { get; init; }

    public IReadOnlyList<ReplayToolCall>? ToolCalls { get; init; }

    public JsonElement? StructuredOutput { get; init; }

    public ReplayUsage? Usage { get; init; }

    public ReplayFailure? Failure { get; init; }

    public static ReplayRecording FromResult(AiTurnResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = string.Concat(result.AssistantMessage.Parts.OfType<TextPart>().Select(p => p.Text));
        return new ReplayRecording
        {
            Stop = result.Stop,
            Text = result.StructuredOutput is null && text.Length > 0 ? text : null,
            ToolCalls = result.ToolCalls.Count > 0
                ? result.ToolCalls.Select(c => new ReplayToolCall(c.CallId, c.ToolName, c.Arguments)).ToList()
                : null,
            StructuredOutput = result.StructuredOutput,
            Usage = new ReplayUsage(
                result.Usage.Model, result.Usage.InputTokens, result.Usage.OutputTokens, result.Usage.CacheReadTokens, result.Usage.CacheWriteTokens),
            Failure = result.Failure is { } failure ? new ReplayFailure(failure.Kind, failure.Message, failure.ValidationErrors) : null,
        };
    }

    /// <summary>The turn as the gateway would have received it from a live provider.</summary>
    public AiTurnResult ToResult(string provider, string routeModel)
    {
        var toolCalls = (ToolCalls ?? []).Select(c => new AiToolCall(c.CallId, c.ToolName, c.Arguments)).ToList();
        var parts = new List<AiContentPart>();
        var text = StructuredOutput is { } output ? output.GetRawText() : Text;
        if (!string.IsNullOrEmpty(text))
        {
            parts.Add(new TextPart(text));
        }

        parts.AddRange(toolCalls.Select(c => new ToolCallPart(c.CallId, c.ToolName, c.Arguments)));

        var usage = Usage is { } u
            ? new AiUsage(provider, u.Model ?? routeModel, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, TimeSpan.Zero, 0m)
            : AiUsage.None(provider, routeModel);
        return new AiTurnResult(
            Stop,
            new AiMessage(AiRole.Assistant, parts),
            toolCalls,
            StructuredOutput,
            usage,
            Failure is { } f ? new AiFailure(f.Kind, f.Message, f.ValidationErrors) : null);
    }
}

public sealed record ReplayToolCall(string CallId, string ToolName, JsonElement Arguments);

public sealed record ReplayUsage(string? Model, int InputTokens, int OutputTokens, int CacheReadTokens = 0, int CacheWriteTokens = 0);

public sealed record ReplayFailure(AiFailureKind Kind, string Message, IReadOnlyList<string>? ValidationErrors = null);
