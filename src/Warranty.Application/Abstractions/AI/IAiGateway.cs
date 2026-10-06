using System.Text.Json;

namespace Warranty.Application.Abstractions.AI;

/// <summary>
/// The only path from the application to any AI model (contracts/ai-gateway.md, constitution VI).
/// Callers never reference a vendor SDK; routes, prompts and providers are configuration.
/// </summary>
public interface IAiGateway
{
    /// <summary>One model turn; the harness drives tool loops by calling this with an append-only conversation.</summary>
    Task<AiTurnResult> CompleteAsync(AiTurnRequest request, CancellationToken ct);

    Task<AiEmbeddingResult> EmbedAsync(AiEmbeddingRequest request, CancellationToken ct);
}

/// <summary>Call attribution supplied by the harness from <see cref="ITenantContext"/>, never from model output.</summary>
public sealed record AiCallContext(Guid TenantId, Guid? ClaimId, Guid? RunId, string Agent, string CorrelationId);

/// <summary>A platform-owned prompt template, e.g. <c>decision</c> v1.</summary>
public sealed record PromptRef(string Id, int Version)
{
    public override string ToString() => $"{Id}.v{Version}";
}

/// <summary>JSON Schema the model output must satisfy (from contracts/schemas).</summary>
public sealed record AiOutputSchema(string SchemaId, JsonElement Schema);

/// <summary>A tool offered to the model, already filtered to the agent's allow-list. No tenant fields.</summary>
public sealed record AiToolDefinition(string Name, string Description, JsonElement InputSchema);

public sealed record AiTurnRequest(
    AiCallContext Context,
    string Route,
    PromptRef Prompt,
    IReadOnlyDictionary<string, string> PromptVariables,
    AiConversation Conversation,
    IReadOnlyList<AiToolDefinition> Tools,
    AiOutputSchema? OutputSchema,
    TimeSpan Timeout,
    int? MaxTokensOverride = null);

public enum AiStopKind
{
    Completed,
    ToolCalls,
    Refused,
    Truncated,
    Failed,
}

public enum AiFailureKind
{
    Transient,
    RateLimited,
    Timeout,
    ProviderError,
    InvalidOutput,
}

public sealed record AiFailure(AiFailureKind Kind, string Message, IReadOnlyList<string>? ValidationErrors = null);

public sealed record AiUsage(
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens,
    TimeSpan Latency,
    decimal EstimatedCost)
{
    public static AiUsage None(string provider, string model) => new(provider, model, 0, 0, 0, 0, TimeSpan.Zero, 0m);
}

public sealed record AiToolCall(string CallId, string ToolName, JsonElement Arguments);

public sealed record AiToolResult(string CallId, JsonElement Result, bool IsError);

/// <param name="MaxTokens">
/// The output token limit the gateway sent with the turn (after route defaults, overrides and model caps), so the
/// harness can retry a <see cref="AiStopKind.Truncated"/> turn with a larger one; null when unknown.
/// </param>
public sealed record AiTurnResult(
    AiStopKind Stop,
    AiMessage AssistantMessage,
    IReadOnlyList<AiToolCall> ToolCalls,
    JsonElement? StructuredOutput,
    AiUsage Usage,
    AiFailure? Failure,
    int? MaxTokens = null);

public sealed record AiEmbeddingRequest(AiCallContext Context, IReadOnlyList<string> Inputs, string Route = "embedding");

public sealed record AiEmbeddingResult(IReadOnlyList<ReadOnlyMemory<float>> Vectors, string Model, int Dimensions, AiUsage Usage);
