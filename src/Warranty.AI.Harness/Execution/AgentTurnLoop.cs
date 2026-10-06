using System.Globalization;
using System.Text;
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

    /// <summary>The reply was cut off at the output token limit, also after the retry with a doubled limit.</summary>
    Truncated,

    Failed,
}

/// <summary>
/// The loop's result; <see cref="LastTurn"/> holds the final structured output or failure and <see cref="Turns"/>
/// counts every model call, the corrective and truncation retries included.
/// </summary>
public sealed record AgentTurnLoopResult(AgentLoopOutcome Outcome, AiTurnResult LastTurn, int Turns, IReadOnlyList<AiUsage> Usage)
{
    /// <summary>The agent status this outcome maps to (FR-031: anything but success goes to human review).</summary>
    public AgentStatus Status => StatusOf(Outcome, LastTurn.Failure);

    /// <summary>
    /// <see cref="AiFailureKind.Timeout"/> → <see cref="AgentStatus.TimedOut"/>, <see cref="AiFailureKind.InvalidOutput"/> →
    /// <see cref="AgentStatus.InvalidOutput"/>, a refusal → <see cref="AgentStatus.Refused"/>; a truncation, the turn limit,
    /// rate limiting and every other provider failure → <see cref="AgentStatus.Failed"/>.
    /// </summary>
    public static AgentStatus StatusOf(AgentLoopOutcome outcome, AiFailure? failure) => outcome switch
    {
        AgentLoopOutcome.Completed => AgentStatus.Succeeded,
        AgentLoopOutcome.Refused => AgentStatus.Refused,
        AgentLoopOutcome.Failed when failure?.Kind == AiFailureKind.Timeout => AgentStatus.TimedOut,
        AgentLoopOutcome.Failed when failure?.Kind == AiFailureKind.InvalidOutput => AgentStatus.InvalidOutput,
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
/// <remarks>
/// Two failures get one more chance (contracts/ai-gateway.md "Failure semantics"); neither retry counts
/// against <see cref="AgentDescriptor.MaxTurns"/>:
/// <list type="bullet">
/// <item><b>Invalid output</b> (<see cref="AiFailureKind.InvalidOutput"/>): one corrective turn — the invalid
/// reply is appended, followed by a user message listing the validation errors. A second invalid reply ends
/// the loop with <see cref="AgentStatus.InvalidOutput"/>.</item>
/// <item><b>Truncation</b> (<see cref="AiStopKind.Truncated"/>): the cut-off reply is discarded and the same
/// turn is asked once more with twice the output token limit (kept for the rest of the loop). A second
/// truncation ends the loop.</item>
/// </list>
/// Refusals, timeouts and other failures end the loop at once. Cancellation (the run deadline) is not a
/// model outcome and propagates to the runner.
/// </remarks>
public sealed class AgentTurnLoop
{
    /// <summary>At most this many validation errors are quoted back to the model in the corrective turn.</summary>
    public const int MaxQuotedErrors = 20;

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

        var calls = 0;
        var turn = 1;
        int? maxTokens = null;
        var corrected = false;
        var retriedTruncation = false;
        string? retry = null;
        while (true)
        {
            calls++;
            var tags = new Dictionary<string, object?> { ["agent"] = agent.Name, ["turn"] = turn, ["route"] = agent.Route };
            if (retry is not null)
            {
                tags["retry"] = retry;
            }

            AiTurnResult result;
            using (ctx.Trace.StartSpan($"agent.{agent.Name}.turn", tags))
            {
                result = await ctx.Gateway.CompleteAsync(
                    new AiTurnRequest(
                        callContext, agent.Route, agent.Prompt, request.PromptVariables, request.Conversation, tools, request.OutputSchema,
                        request.TurnTimeout, maxTokens),
                    ct);
            }

            usage.Add(result.Usage);
            retry = null;
            if (result.Stop == AiStopKind.Failed)
            {
                if (result.Failure?.Kind == AiFailureKind.InvalidOutput && !corrected)
                {
                    corrected = true;
                    retry = "corrective";
                    AppendCorrection(request.Conversation, result);
                    continue;
                }

                return new AgentTurnLoopResult(AgentLoopOutcome.Failed, result, calls, usage);
            }

            if (result.Stop == AiStopKind.Truncated && !retriedTruncation)
            {
                // The cut-off reply is not appended: the same turn is asked again with a larger output limit.
                retriedTruncation = true;
                retry = "truncation";
                maxTokens = Doubled(result, maxTokens);
                continue;
            }

            request.Conversation.AddAssistant(result.AssistantMessage);
            switch (result.Stop)
            {
                case AiStopKind.Completed:
                    return new AgentTurnLoopResult(AgentLoopOutcome.Completed, result, calls, usage);
                case AiStopKind.Refused:
                    return new AgentTurnLoopResult(AgentLoopOutcome.Refused, result, calls, usage);
                case AiStopKind.Truncated:
                    return new AgentTurnLoopResult(AgentLoopOutcome.Truncated, result, calls, usage);
                case AiStopKind.ToolCalls when result.ToolCalls.Count == 0:
                    return new AgentTurnLoopResult(
                        AgentLoopOutcome.Failed,
                        result with { Failure = new AiFailure(AiFailureKind.ProviderError, "The model stopped for tool use without tool calls.") },
                        calls,
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
                return new AgentTurnLoopResult(AgentLoopOutcome.MaxTurnsReached, result, calls, usage);
            }

            turn++;
        }
    }

    /// <summary>
    /// The corrective turn: the invalid reply (when it has content) and a user message quoting the validation
    /// errors and asking for the corrected output only.
    /// </summary>
    private static void AppendCorrection(AiConversation conversation, AiTurnResult invalid)
    {
        if (invalid.AssistantMessage.Parts.Count > 0)
        {
            conversation.AddAssistant(invalid.AssistantMessage);
        }

        var failure = invalid.Failure!;
        var text = new StringBuilder();
        text.Append("Your previous reply was rejected: ").Append(failure.Message).Append('\n');
        var errors = failure.ValidationErrors ?? [];
        if (errors.Count > 0)
        {
            text.Append("Validation errors:\n");
            foreach (var error in errors.Take(MaxQuotedErrors))
            {
                text.Append("- ").Append(error).Append('\n');
            }

            if (errors.Count > MaxQuotedErrors)
            {
                text.Append(CultureInfo.InvariantCulture, $"- ... and {errors.Count - MaxQuotedErrors} more.\n");
            }
        }

        text.Append("Reply again with only the corrected output: a single JSON value that satisfies the required schema.");
        conversation.AddUser(new TextPart(text.ToString()));
    }

    /// <summary>
    /// Twice the limit the truncated turn ran with: the limit the gateway reported, else the output tokens the turn
    /// used (a truncated turn used them all), else twice the previous override; null keeps the route default.
    /// </summary>
    private static int? Doubled(AiTurnResult truncated, int? previous)
    {
        var basis = truncated.MaxTokens is > 0
            ? truncated.MaxTokens
            : truncated.Usage.OutputTokens > 0 ? truncated.Usage.OutputTokens : previous;
        return basis is { } tokens ? (int)Math.Min((long)tokens * 2, int.MaxValue) : null;
    }
}
