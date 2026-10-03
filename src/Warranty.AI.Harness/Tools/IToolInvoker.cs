using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Harness.Tools;

/// <summary>
/// Executes tool calls for one agent of one run (contracts/agents-and-tools.md). Scoped to the
/// agent's allow-list: <see cref="Definitions"/> is what the model is offered, and every invocation is
/// checked again. A denied, unknown or failing call is returned as an error result to the model, never thrown.
/// </summary>
public interface IToolInvoker
{
    /// <summary>The ReadOnly tools this agent may call, as offered to the model.</summary>
    IReadOnlyList<AiToolDefinition> Definitions { get; }

    Task<AiToolResult> InvokeAsync(AiToolCall call, CancellationToken ct);
}

/// <summary>An invoker for agents without tools.</summary>
public sealed class NoToolInvoker : IToolInvoker
{
    public static NoToolInvoker Instance { get; } = new();

    public IReadOnlyList<AiToolDefinition> Definitions => [];

    public Task<AiToolResult> InvokeAsync(AiToolCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        return Task.FromResult(new AiToolResult(
            call.CallId,
            System.Text.Json.JsonSerializer.SerializeToElement(new { error = $"Tool '{call.ToolName}' is not available." }),
            IsError: true));
    }
}
