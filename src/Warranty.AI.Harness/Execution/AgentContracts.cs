using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Tools;
using Warranty.Application.Abstractions.AI;

namespace Warranty.AI.Harness.Execution;

/// <summary>An agent of the harness: typed input, typed output, one route (contracts/agents-and-tools.md).</summary>
public interface IAgent<in TInput, TOutput>
{
    AgentDescriptor Descriptor { get; }

    Task<AgentResult<TOutput>> RunAsync(TInput input, AgentExecutionContext ctx, CancellationToken ct);
}

/// <summary>
/// Static description of an agent. Agents name a gateway <paramref name="Route"/>, never a model.
/// </summary>
/// <param name="Name"><c>intake</c>, <c>evidence</c>, <c>policy</c> or <c>decision</c>; also the tool caller name.</param>
/// <param name="Route">Gateway route.</param>
/// <param name="Prompt">Platform-owned prompt template and version.</param>
/// <param name="OutputSchemaId"><c>$id</c> of the output schema in contracts/schemas, if the agent returns structured output.</param>
/// <param name="AllowedTools">Tool names this agent may be offered and may call.</param>
/// <param name="MaxTurns">Upper bound of model turns per call of the agent.</param>
/// <param name="InputTokenBudget">Token budget of the assembled user turn (attachments excluded).</param>
public sealed record AgentDescriptor(
    string Name,
    string Route,
    PromptRef Prompt,
    string? OutputSchemaId,
    IReadOnlyList<string> AllowedTools,
    int MaxTurns,
    int InputTokenBudget)
{
    public string Name { get; } = !string.IsNullOrWhiteSpace(Name) ? Name : throw new ArgumentException("An agent needs a name.", nameof(Name));

    public string Route { get; } = !string.IsNullOrWhiteSpace(Route) ? Route : throw new ArgumentException("An agent needs a route.", nameof(Route));

    public int MaxTurns { get; } = MaxTurns > 0 ? MaxTurns : throw new ArgumentOutOfRangeException(nameof(MaxTurns), MaxTurns, "At least one turn.");

    public int InputTokenBudget { get; } = InputTokenBudget > 0
        ? InputTokenBudget
        : throw new ArgumentOutOfRangeException(nameof(InputTokenBudget), InputTokenBudget, "A positive token budget is required.");
}

public enum AgentStatus
{
    Succeeded,
    InvalidOutput,
    Failed,
    Refused,
    TimedOut,
}

/// <summary>An agent's outcome; anything but <see cref="AgentStatus.Succeeded"/> routes the claim to human review (FR-031).</summary>
public sealed record AgentResult<T>(AgentStatus Status, T? Output, IReadOnlyList<string> Diagnostics)
{
    public bool Succeeded => Status == AgentStatus.Succeeded;

    public static AgentResult<T> Success(T output, params string[] diagnostics) => new(AgentStatus.Succeeded, output, diagnostics);

    public static AgentResult<T> Failure(AgentStatus status, params string[] diagnostics)
        => status == AgentStatus.Succeeded
            ? throw new ArgumentException("A failure needs a failing status.", nameof(status))
            : new AgentResult<T>(status, default, diagnostics);
}

/// <summary>What an agent may use while it runs: the run state, the gateway, its scoped tools and tracing.</summary>
public sealed class AgentExecutionContext(AdjudicationContext run, IAiGateway gateway, IToolInvoker tools, ITraceWriter trace)
{
    public AdjudicationContext Run { get; } = run ?? throw new ArgumentNullException(nameof(run));

    public IAiGateway Gateway { get; } = gateway ?? throw new ArgumentNullException(nameof(gateway));

    /// <summary>Already scoped to this agent's allow-list.</summary>
    public IToolInvoker Tools { get; } = tools ?? throw new ArgumentNullException(nameof(tools));

    public ITraceWriter Trace { get; } = trace ?? throw new ArgumentNullException(nameof(trace));

    /// <summary>Call attribution for the gateway, from the run's tenant context — never from model output.</summary>
    public AiCallContext CallContextFor(string agent)
        => new(Run.Tenant.TenantId, Run.ClaimId, Run.RunId, agent, Run.Tenant.CorrelationId);
}
