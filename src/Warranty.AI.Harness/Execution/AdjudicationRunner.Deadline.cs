using System.Globalization;
using Warranty.AI.Harness.Agents;
using Warranty.Domain.Adjudication;

namespace Warranty.AI.Harness.Execution;

public sealed partial class AdjudicationRunner
{
    /// <summary>
    /// The run deadline (contracts/agents-and-tools.md "Budgets and limits", spec FR-031): AI work not finished
    /// within it counts as AI analysis that could not be completed, and the claim goes to human review.
    /// </summary>
    public static readonly TimeSpan RunDeadline = TimeSpan.FromMinutes(4);

    /// <summary>The detail of the <c>AiStepFailed</c> entry of a step stopped (or not started) by the run deadline.</summary>
    public static readonly string DeadlineExceeded = string.Create(
        CultureInfo.InvariantCulture, $"Timeout: the run exceeded its {RunDeadline.TotalMinutes:0}-minute deadline.");

    /// <summary>
    /// Runs one agent within the run deadline. The agent gets a token linked to the job's and to the deadline
    /// timer; when the deadline cancels it (or has passed before the step), the step is a
    /// <see cref="AgentStatus.TimedOut"/> failure like any other AI failure. Cancellation of the job itself
    /// still propagates. Only the AI steps run under the deadline: the deterministic risk, guardrail and action
    /// steps always complete, so the claim reaches human review.
    /// </summary>
    private async Task<AgentResult<TOut>> RunAgentAsync<TIn, TOut>(RunState state, IAgent<TIn, TOut> agent, TIn input, CancellationToken ct)
    {
        var ctx = AgentContext(state.Context, agent.Descriptor.Name);
        if (state.Deadline is not { } deadline)
        {
            return await agent.RunAsync(input, ctx, ct);
        }

        if (deadline.Expired)
        {
            return AgentResult<TOut>.Failure(AgentStatus.TimedOut, DeadlineExceeded);
        }

        try
        {
            return await agent.RunAsync(input, ctx, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.Expired && !ct.IsCancellationRequested)
        {
            return AgentResult<TOut>.Failure(AgentStatus.TimedOut, DeadlineExceeded);
        }
    }

    /// <summary>
    /// The intake result of an intake step that produced none (the deadline stopped it before the agent stored
    /// its result): no checks, no extraction, nothing missing. Added to the step's unit of work so a resumed run
    /// finds it; the recorded failure sends the claim to human review.
    /// </summary>
    private IntakeResult NoIntakeResult(RunState state)
    {
        var result = IntakeResult.Create(state.Context.RunId, tenant.TenantId, [], IntakeAgent.NoExtraction, []);
        adjudication.AddIntakeResult(result);
        return result;
    }

    /// <summary>The run deadline of one execution of the runner: a timer on the runner's clock linked to the job's token.</summary>
    private sealed class Deadline : IDisposable
    {
        private readonly CancellationTokenSource _timer;
        private readonly CancellationTokenSource _linked;

        public Deadline(TimeSpan after, TimeProvider time, CancellationToken ct)
        {
            _timer = new CancellationTokenSource(after, time);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _timer.Token);
        }

        public CancellationToken Token => _linked.Token;

        public bool Expired => _timer.IsCancellationRequested;

        public void Dispose()
        {
            _linked.Dispose();
            _timer.Dispose();
        }
    }
}
