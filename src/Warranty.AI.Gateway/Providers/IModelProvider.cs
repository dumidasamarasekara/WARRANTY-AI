using Warranty.AI.Gateway.Prompts;
using Warranty.AI.Gateway.Routing;
using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Gateway.Providers;

/// <summary>
/// A chat model provider behind the gateway (contracts/ai-gateway.md): <c>anthropic</c>, <c>replay</c>,
/// or a scripted provider in tests. Providers translate the provider-neutral request, map provider
/// stop reasons and errors to <see cref="AiTurnResult"/> and report raw token usage; the gateway adds
/// latency, cost, validation, rate limiting and records.
/// </summary>
internal interface IModelProvider
{
    string Name { get; }

    Task<AiTurnResult> CompleteAsync(ResolvedTurnRequest request, CancellationToken ct);
}

/// <summary>
/// A turn ready for a provider: the route and model profile, the rendered system prompt and the
/// conversation with personal data already redacted from its text parts.
/// </summary>
internal sealed record ResolvedTurnRequest(
    AiTurnRequest Request,
    ResolvedRoute Route,
    PromptTemplate Prompt,
    string SystemPrompt,
    IReadOnlyList<AiMessage> Messages,
    int MaxTokens);
