using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Harness.Execution;

/// <summary>One agent conversation to drive: the descriptor, the conversation with its first user turn, and prompt inputs.</summary>
public sealed record AgentTurnLoopRequest(
    AgentDescriptor Agent,
    AiConversation Conversation,
    IReadOnlyDictionary<string, string> PromptVariables,
    AiOutputSchema? OutputSchema,
    TimeSpan TurnTimeout)
{
    public static readonly TimeSpan DefaultTurnTimeout = TimeSpan.FromSeconds(90);
}

/// <summary>How a loop ended.</summary>
public enum AgentLoopOutcome
{
    /// <summary>The model finished with a final answer (structured output when a schema was given).</summary>
    Completed,

    /// <summary>The model still asked for tools after <see cref="AgentDescriptor.MaxTurns"/> turns.</summary>
    MaxTurnsReached,

    Refused,

    Truncated,

    Failed,
}

/// <summary>The loop's result; <see cref="LastTurn"/> holds the final structured output or failure.</summary>
public sealed record AgentTurnLoopResult(AgentLoopOutcome Outcome, AiTurnResult LastTurn, int Turns, IReadOnlyList<AiUsage> Usage)
{
    /// <summary>The agent status this outcome maps to (FR-031: anything but success goes to human review).</summary>
    public AgentStatus Status => Outcome switch
    {
        AgentLoopOutcome.Completed => AgentStatus.Succeeded,
        AgentLoopOutcome.Refused => AgentStatus.Refused,
        AgentLoopOutcome.Failed when LastTurn.Failure?.Kind == AiFailureKind.Timeout => AgentStatus.TimedOut,
        AgentLoopOutcome.Failed when LastTurn.Failure?.Kind == AiFailureKind.InvalidOutput => AgentStatus.InvalidOutput,
        _ => AgentStatus.Failed,
    };
}

/// <summary>
/// The bounded model/tool loop of one agent (contracts/agents-and-tools.md). Each turn calls
/// <see cref="IAiGateway.CompleteAsync"/> with the append-only conversation and the agent's scoped tools;
/// the assistant message is appended exactly as returned (opaque provider blocks included). When the
/// model asks for tools, every call of that turn is executed — in order, so reference numbering is
/// deterministic — and all results go back in one message. The loop stops at a final answer, a
/// refusal, a truncation, a failure, or after <see cref="AgentDescriptor.MaxTurns"/> turns.
/// </summary>
public sealed class AgentTurnLoop
{
    public async Task<AgentTurnLoopResult> RunAsync(AgentTurnLoopRequest request, AgentExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ctx);
        if (request.Conversation.Messages.Count == 0)
        {
            throw new ArgumentException("The conversation needs the agent's first user turn.", nameof(request));
        }

        var agent = request.Agent;
        var usage = new List<AiUsage>();
        var callContext = ctx.CallContextFor(agent.Name);
        var tools = ctx.Tools.Definitions.Where(t => agent.AllowedTools.Contains(t.Name, StringComparer.Ordinal)).ToList();

        for (var turn = 1; ; turn++)
        {
            AiTurnResult result;
            using (ctx.Trace.StartSpan($"agent.{agent.Name}.turn", new Dictionary<string, object?> { ["agent"] = agent.Name, ["turn"] = turn, ["route"] = agent.Route }))
            {
                result = await ctx.Gateway.CompleteAsync(
                    new AiTurnRequest(
                        callContext, agent.Route, agent.Prompt, request.PromptVariables, request.Conversation, tools, request.OutputSchema, request.TurnTimeout),
                    ct);
            }

            usage.Add(result.Usage);
            if (result.Stop == AiStopKind.Failed)
            {
                return new AgentTurnLoopResult(AgentLoopOutcome.Failed, result, turn, usage);
            }

            request.Conversation.AddAssistant(result.AssistantMessage);
            switch (result.Stop)
            {
                case AiStopKind.Completed:
                    return new AgentTurnLoopResult(AgentLoopOutcome.Completed, result, turn, usage);
                case AiStopKind.Refused:
                    return new AgentTurnLoopResult(AgentLoopOutcome.Refused, result, turn, usage);
                case AiStopKind.Truncated:
                    return new AgentTurnLoopResult(AgentLoopOutcome.Truncated, result, turn, usage);
                case AiStopKind.ToolCalls when result.ToolCalls.Count == 0:
                    return new AgentTurnLoopResult(
                        AgentLoopOutcome.Failed,
                        result with { Failure = new AiFailure(AiFailureKind.ProviderError, "The model stopped for tool use without tool calls.") },
                        turn,
                        usage);
            }

            // Tools run even on the last allowed turn so the transcript stays well-formed for the trace.
            var results = new List<AiToolResult>(result.ToolCalls.Count);
            foreach (var call in result.ToolCalls)
            {
                using (ctx.Trace.StartSpan($"tool.{call.ToolName}", new Dictionary<string, object?> { ["agent"] = agent.Name, ["tool"] = call.ToolName }))
                {
                    results.Add(await ctx.Tools.InvokeAsync(call, ct));
                }
            }

            request.Conversation.AddToolResults(results);
            if (turn >= agent.MaxTurns)
            {
                return new AgentTurnLoopResult(AgentLoopOutcome.MaxTurnsReached, result, turn, usage);
            }
        }
    }
}
