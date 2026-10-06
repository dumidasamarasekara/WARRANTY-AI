using System.Text.Json;
using System.Text.Json.Serialization;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Agents.Risk;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Tools;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Adjudication;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Actions;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Guardrails;
using Warranty.Guardrails.Pipeline;

namespace Warranty.AI.Harness.Execution;

/// <summary>
/// Runs or resumes the harness for one claim round (contracts/agents-and-tools.md "Harness lifecycle"):
/// <c>LoadCase → Intake → (Evidence ‖ Policy) → Risk → Decision → Guardrails → Action</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Checkpoints.</b> Agents only add their rows to the scoped unit of work; after each step the runner
/// records the reference map, moves <c>adjudication_runs.current_step</c> to the next step and commits the
/// step's rows, the checkpoint and the step's decision-trail entries in one transaction. A retried job
/// therefore resumes after the last committed step: completed steps are not run again and their outputs
/// are read back from the database (<see cref="IAdjudicationRepository.GetRunRecordAsync"/>), together with
/// the run's <c>EV-n</c>/<c>POL-n</c>/<c>GLB-n</c> map. Guardrails and Action commit together, so a run is
/// never left with an evaluation whose action did not execute.
/// </para>
/// <para>
/// <b>Evidence ‖ Policy run one after the other.</b> Both steps only need the intake result, but they write
/// through the run's scoped repositories, tool invoker and gateway usage recorder, which share one EF Core
/// <c>DbContext</c>, and a <c>DbContext</c> is not thread-safe. Running them in separate DI scopes would split
/// one checkpoint into two units of work — an infrastructure failure in one branch would leave the other
/// branch's rows committed without a checkpoint, and the retried job would store them twice — and the
/// Decision step must mark the cited clauses on the <see cref="RetrievedPolicyRef"/> instances tracked by
/// the run's own unit of work. So the runner runs Evidence and then Policy; the Evidence step still
/// analyses its photos in parallel, and the reference numbering is unaffected (EV-n and POL-n have separate
/// counters, and replay fixtures number calls per agent).
/// </para>
/// <para>
/// <b>AI failures are outcomes</b> (FR-031): a refused, timed-out, failed or invalid step is recorded on the
/// run (<c>failure_reason</c>) and as an <c>AiStepFailed</c> trail entry, the later model steps are skipped
/// (the claim cannot be finalized any more), the deterministic risk assessment still runs, and the guardrails
/// receive the unavailable outputs as null, which escalates the claim to human review
/// (<c>AI_UNAVAILABLE</c> / <c>INVALID_RECOMMENDATION</c>). Only infrastructure exceptions escape, so the
/// job retries the round from its checkpoint.
/// </para>
/// <para>
/// <b>Intake short-circuit.</b> When intake lists missing items, the AI analysis steps do not run: the
/// intake-stage risk assessment and the guardrails decide between a request for information and review.
/// </para>
/// </remarks>
public sealed partial class AdjudicationRunner(
    ITenantContext tenant,
    ICaseKnowledgeProvider cases,
    IClaimRepository claims,
    IAdjudicationRepository adjudication,
    IPolicyRepository policies,
    ITenantRepository tenants,
    IUnitOfWork unitOfWork,
    IDecisionTrailWriter trail,
    AdjudicationAgents agents,
    IRiskAssessor risk,
    IGuardrailEngine guardrails,
    IActionExecutor actions,
    IAiGateway gateway,
    ToolInvoker tools,
    ITraceWriter traces,
    IPiiRedactor redactor,
    TimeProvider time) : IAdjudicationRunner
{
    /// <summary>Trail actor of the deterministic steps.</summary>
    public const string SystemActor = "system";

    private static readonly JsonSerializerOptions ActionJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task RunAsync(Guid claimId, int round, CancellationToken ct)
    {
        if (!tenant.IsResolved)
        {
            throw new InvalidOperationException("An adjudication run needs the tenant context of its job.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(round);
        using var runSpan = traces.StartSpan("harness.run", Tags(claimId, round, null, null));

        var state = await LoadCaseAsync(claimId, round, ct);
        if (state is null)
        {
            return;
        }

        if (state.Run.CurrentStep == RunStep.Intake)
        {
            await IntakeAsync(state, ct);
        }

        if (state.ShortCircuit)
        {
            if (state.Run.CurrentStep < RunStep.Guardrails)
            {
                await IntakeRiskAsync(state, ct);
            }
        }
        else
        {
            if (state.Run.CurrentStep == RunStep.Evidence)
            {
                await EvidenceAsync(state, ct);
            }

            if (state.Run.CurrentStep == RunStep.Policy)
            {
                await PolicyAsync(state, ct);
            }

            if (state.Run.CurrentStep == RunStep.Risk)
            {
                await RiskAsync(state, ct);
            }

            if (state.Run.CurrentStep == RunStep.Decision)
            {
                await DecisionAsync(state, ct);
            }
        }

        await GuardrailsAndActionAsync(state, ct);
    }

    // ---- Steps -------------------------------------------------------------------------------------

    /// <summary>
    /// Creates the run (or loads it to resume), moves the claim to <c>UnderEvaluation</c> and builds the
    /// execution state; null when the run already completed (a duplicate job) and there is nothing to do.
    /// </summary>
    private async Task<RunState?> LoadCaseAsync(Guid claimId, int round, CancellationToken ct)
    {
        using var span = traces.StartSpan("harness.step.load_case", Tags(claimId, round, null, "load_case"));
        var claim = await claims.GetAsync(claimId, ct)
                    ?? throw new InvalidOperationException($"Claim {claimId} is not visible in the current tenant.");
        var run = await adjudication.GetRunAsync(claimId, round, ct);
        switch (run?.Status)
        {
            case RunStatus.Completed:
                return null;
            case RunStatus.Failed:
                throw new InvalidOperationException($"Run {run.Id} of claim {claimId} round {round} failed and cannot be resumed.");
        }

        if (claim.CurrentRound != round)
        {
            throw new InvalidOperationException($"Claim {claimId} is in round {claim.CurrentRound}; the job asked for round {round}.");
        }

        var now = time.GetUtcNow();
        if (run is null)
        {
            run = AdjudicationRun.Start(Guid.CreateVersion7(), tenant.TenantId, claimId, round, tenant.CorrelationId, now);
            adjudication.AddRun(run);
        }

        claim.StartEvaluation(now);
        await unitOfWork.SaveChangesAsync(ct);

        var @case = await cases.GetCaseContextAsync(claimId, round, ct);
        return await RestoreAsync(run, @case, ct);
    }

    private async Task IntakeAsync(RunState state, CancellationToken ct)
    {
        using var span = StartStep(state, "intake");
        var ctx = state.Context;
        var agent = agents.Intake;
        var result = await agent.RunAsync(ctx.Case, AgentContext(ctx, agent.Descriptor.Name), ct);
        var intake = result.Output ?? throw new InvalidOperationException("The Intake Agent returned no intake result.");
        ctx.Intake = intake;
        state.ShortCircuit = intake.MissingItems.Count > 0;

        var entries = IntakeEntries(state, intake);
        if (!result.Succeeded)
        {
            RecordFailure(state, AgentNames.Intake, result, entries);
        }

        // Missing items short-circuit to the intake-stage risk; a failed intake leaves nothing to analyse.
        var next = state.ShortCircuit || state.Failed(AgentNames.Intake) ? RunStep.Risk : RunStep.Evidence;
        await CommitAsync(state, next, entries, ct);
    }

    private async Task EvidenceAsync(RunState state, CancellationToken ct)
    {
        using var span = StartStep(state, "evidence");
        var ctx = state.Context;
        var agent = agents.Evidence;
        var result = await agent.RunAsync(new EvidenceInput(ctx.Case, RequireIntake(ctx)), AgentContext(ctx, agent.Descriptor.Name), ct);
        ctx.Evidence = result.Output;

        var entries = new List<TrailEntry>();
        if (result.Output is { } evidence)
        {
            entries.Add(EvidenceEntry(state, evidence));
        }

        if (!result.Succeeded || result.Output is null)
        {
            RecordFailure(state, AgentNames.Evidence, result, entries);
        }

        await CommitAsync(state, RunStep.Policy, entries, ct);
    }

    private async Task PolicyAsync(RunState state, CancellationToken ct)
    {
        using var span = StartStep(state, "policy");
        var ctx = state.Context;
        var agent = agents.Policy;
        var result = await agent.RunAsync(new PolicyInput(ctx.Case, RequireIntake(ctx)), AgentContext(ctx, agent.Descriptor.Name), ct);

        // The partial result is kept even when the model step failed: its clauses are the run's issued POL-n.
        ctx.Policy = result.Output;

        var entries = new List<TrailEntry>();
        if (result.Output is { } policy)
        {
            entries.AddRange(PolicyEntries(state, policy));
        }

        if (!result.Succeeded || result.Output is null)
        {
            RecordFailure(state, AgentNames.Policy, result, entries);
        }

        await CommitAsync(state, RunStep.Risk, entries, ct);
    }

    /// <summary>The deterministic risk signals the Decision step lists; nothing is stored until the full assessment.</summary>
    private async Task RiskAsync(RunState state, CancellationToken ct)
    {
        using var span = StartStep(state, "risk");
        state.Signals = await risk.DetectDeterministicSignalsAsync(state.Context, ct);
        await CommitAsync(state, RunStep.Decision, [], ct);
    }

    /// <summary>The intake short-circuit's risk step: the signals known without evidence analysis (stage <c>Intake</c>).</summary>
    private async Task IntakeRiskAsync(RunState state, CancellationToken ct)
    {
        using var span = StartStep(state, "risk");
        var ctx = state.Context;
        var assessment = await risk.AssessAtIntakeAsync(ctx.Case, RequireIntake(ctx), ct);
        ctx.Risk = assessment;
        await CommitAsync(state, RunStep.Guardrails, [RiskEntry(state, assessment)], ct);
    }

    /// <summary>
    /// The recommendation (skipped after an earlier AI failure) and then the run's one full risk assessment,
    /// from the deterministic signals and what the Evidence and Decision outputs reported.
    /// </summary>
    private async Task DecisionAsync(RunState state, CancellationToken ct)
    {
        using var span = StartStep(state, "decision");
        var ctx = state.Context;
        var signals = state.Signals ?? await risk.DetectDeterministicSignalsAsync(ctx, ct);

        var failures = new List<TrailEntry>();
        var modelRisk = AiRiskReading.None;
        if (state.CanRunDecision)
        {
            var agent = agents.Decision;
            var input = DecisionInput.From(ctx, ctx.Policy?.CoverageWindow ?? PolicyResult.NoCoverageWindow, signals);
            var result = await agent.RunAsync(input, AgentContext(ctx, agent.Descriptor.Name), ct);
            ctx.Recommendation = result.Output;
            modelRisk = result.Output?.Risk ?? AiRiskReading.None;
            if (!result.Succeeded)
            {
                RecordFailure(state, AgentNames.Decision, result, failures);
            }
        }

        IReadOnlyList<RiskSignal> evidenceSignals = ctx.Evidence is { } evidence ? EvidenceReadings.AiRiskSignals(evidence, ctx.References) : [];
        var assessment = await risk.AssessFullAsync(ctx, new AiRiskReading(modelRisk.ModelLevel, [.. modelRisk.Signals, .. evidenceSignals]), ct);
        ctx.Risk = assessment;

        var entries = new List<TrailEntry> { RiskEntry(state, assessment) };
        if (ctx.Recommendation is { } recommendation)
        {
            entries.Add(RecommendationEntry(state, recommendation.Recommendation));
        }

        entries.AddRange(failures);
        await CommitAsync(state, RunStep.Guardrails, entries, ct);
    }

    /// <summary>
    /// Evaluates the guardrails and executes the issued action in one transaction: the evaluation, the
    /// <c>GuardrailsEvaluated</c> entry (written before the action, which cross-checks the stored
    /// disposition), the action's own state change and trail entries, and the completed run.
    /// </summary>
    private async Task GuardrailsAndActionAsync(RunState state, CancellationToken ct)
    {
        var ctx = state.Context;
        GuardrailOutcome outcome;
        ApprovedAction action;
        GuardrailEvaluation evaluation;
        using (StartStep(state, "guardrails"))
        {
            var settings = await tenants.GetCurrentSettingsAsync(ct);
            outcome = guardrails.Evaluate(GuardrailInputOf(state, settings));
            action = outcome.Action ?? throw new InvalidOperationException("The guardrail engine issued no action.");
            evaluation = ctx.Guardrails ?? GuardrailEvaluation.Create(
                ctx.RunId, tenant.TenantId, outcome.Checks, outcome.Disposition, outcome.Reasons, JsonSerializer.Serialize(action, ActionJson), time.GetUtcNow());
        }

        var stored = ctx.Guardrails is not null;
        ctx.Guardrails = evaluation;
        await unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                if (!stored)
                {
                    adjudication.AddGuardrailEvaluation(evaluation);
                }

                state.Run.RecordReferences(ctx.References.ToMap());
                state.Run.AdvanceTo(RunStep.Action);
                var entry = GuardrailsEntry(state, outcome, action);
                await trail.AppendAsync(ctx.ClaimId, entry.Step, entry.Actor, entry.Summary, entry.Payload, token);

                using (StartStep(state, "action"))
                {
                    await actions.ExecuteAsync(action, token);
                }

                state.Run.Complete(outcome.Disposition, time.GetUtcNow());
            },
            ct);
    }

    // ---- Guardrail input -------------------------------------------------------------------------------

    /// <summary>
    /// The guardrail engine's view of the run. A step that failed (or was skipped after a failure) is passed as
    /// null, which the checks treat as "did not complete"; the Evidence step's <c>INVOICE_LEGIBLE</c> joins the
    /// intake validation (<see cref="IntakeResult"/> itself is immutable).
    /// </summary>
    private GuardrailInput GuardrailInputOf(RunState state, Domain.Tenancy.TenantSettings settings)
    {
        var ctx = state.Context;
        var intake = RequireIntake(ctx);
        var evidence = state.EvidenceCompleted ? ctx.Evidence : null;
        var policy = state.PolicyCompleted ? ctx.Policy : null;

        // A product/serial pair missing from the tenant's catalog has no product and so no claim value (never one
        // borrowed from the model code alone or another tenant): PRODUCT_IN_CATALOG and CLAIM_VALUE_WITHIN_LIMIT fail.
        var facts = new CaseFacts(
            tenant.TenantId,
            ctx.ClaimId,
            ctx.RunId,
            ctx.Case.Product is not null,
            ctx.Case.Product?.Category,
            ctx.Case.Product?.ClaimValue,
            ctx.Case.ReviewerInfoRequested,
            ctx.Case.AutoInfoRequestCount);

        return new GuardrailInput(
            settings,
            facts,
            WithEvidenceValidation(intake, evidence),
            evidence is null ? null : EvidenceReadings.ToGuardrailFacts(evidence, ctx.References),
            policy?.ToFacts(),
            ctx.Risk,
            ctx.Recommendation?.Recommendation,
            ctx.References.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal),
            ActorInfo.Automation,
            ctx.Case.ClaimDate);
    }

    private static IntakeResult WithEvidenceValidation(IntakeResult intake, EvidenceResult? evidence)
    {
        if (evidence is not { Validation.Count: > 0 })
        {
            return intake;
        }

        var judged = evidence.Validation.Select(v => v.Check).ToHashSet(StringComparer.Ordinal);
        return IntakeResult.Create(
            intake.RunId,
            intake.TenantId,
            [.. intake.Validation.Where(v => !judged.Contains(v.Check)), .. evidence.Validation],
            intake.ExtractionJson,
            intake.MissingItems);
    }

    // ---- Plumbing --------------------------------------------------------------------------------------

    /// <summary>Records the reference map and the next checkpoint and commits them with the step's rows and trail entries.</summary>
    private async Task CommitAsync(RunState state, RunStep next, IReadOnlyList<TrailEntry> entries, CancellationToken ct)
    {
        state.Run.RecordReferences(state.Context.References.ToMap());
        state.Run.AdvanceTo(next);
        if (entries.Count == 0)
        {
            await unitOfWork.SaveChangesAsync(ct);
            return;
        }

        await unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                foreach (var entry in entries)
                {
                    await trail.AppendAsync(state.Context.ClaimId, entry.Step, entry.Actor, entry.Summary, entry.Payload, token);
                }
            },
            ct);
    }

    /// <summary>Records an AI step failure on the run (FR-031) and adds its <c>AiStepFailed</c> entry.</summary>
    private void RecordFailure<T>(RunState state, string agent, AgentResult<T> result, List<TrailEntry> entries)
    {
        var status = result.Succeeded ? AgentStatus.Failed : result.Status;
        var detail = redactor.Redact(result.Diagnostics.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)) ?? $"The step ended with {status}.").Text;
        var failure = new AiStepFailure(agent, status, detail);
        state.Failures.Add(failure);
        state.Run.RecordAiFailure(AiStepFailure.Format(state.Failures));
        entries.Add(FailureEntry(state, failure));
    }

    private AgentExecutionContext AgentContext(AdjudicationContext run, string agent) => new(run, gateway, tools.For(agent, run), traces);

    private IDisposable? StartStep(RunState state, string step)
        => traces.StartSpan($"harness.step.{step}", Tags(state.Context.ClaimId, state.Context.Round, state.Context.RunId, step));

    private Dictionary<string, object?> Tags(Guid claimId, int round, Guid? runId, string? step)
    {
        var tags = new Dictionary<string, object?>
        {
            ["warranty.claim_id"] = claimId,
            ["warranty.round"] = round,
            ["warranty.tenant_id"] = tenant.TenantId,
            ["warranty.correlation_id"] = tenant.CorrelationId,
        };
        if (runId is { } id)
        {
            tags["warranty.run_id"] = id;
        }

        if (step is not null)
        {
            tags["warranty.step"] = step;
        }

        return tags;
    }

    private static IntakeResult RequireIntake(AdjudicationContext ctx)
        => ctx.Intake ?? throw new InvalidOperationException($"Run {ctx.RunId} has no intake result.");

    /// <summary>The run being executed and what the runner knows beyond <see cref="AdjudicationContext"/>.</summary>
    private sealed class RunState(AdjudicationRun run, AdjudicationContext context, IEnumerable<AiStepFailure> failures)
    {
        public AdjudicationRun Run { get; } = run;

        public AdjudicationContext Context { get; } = context;

        /// <summary>AI step failures so far, in order (also on the run's <c>failure_reason</c>).</summary>
        public List<AiStepFailure> Failures { get; } = [.. failures];

        /// <summary>Intake listed missing items: the AI analysis steps do not run.</summary>
        public bool ShortCircuit { get; set; }

        /// <summary>The Risk step's deterministic signals; recomputed when a resumed run starts at Decision.</summary>
        public IReadOnlyList<RiskSignal>? Signals { get; set; }

        public bool Failed(string agent) => Failures.Exists(f => f.Agent == agent);

        /// <summary>Evidence and Policy run only after a complete intake; Decision only after both completed.</summary>
        public bool CanRunDecision => Failures.Count == 0 && Context.Intake is not null && Context.Evidence is not null && Context.Policy is not null;

        public bool EvidenceCompleted => !ShortCircuit && !Failed(AgentNames.Intake) && !Failed(AgentNames.Evidence) && Context.Evidence is not null;

        public bool PolicyCompleted => !ShortCircuit && !Failed(AgentNames.Intake) && !Failed(AgentNames.Policy) && Context.Policy is not null;
    }

    private sealed record TrailEntry(Domain.Common.TrailStep Step, string Actor, string Summary, object Payload);
}

/// <summary>The four agents the runner calls (resolved as their <see cref="IAgent{TInput, TOutput}"/> registrations).</summary>
public sealed record AdjudicationAgents(
    IAgent<CaseContext, IntakeResult> Intake,
    IAgent<EvidenceInput, EvidenceResult> Evidence,
    IAgent<PolicyInput, PolicyResult> Policy,
    IAgent<DecisionInput, RecommendationResult> Decision);

/// <summary>
/// One failed AI step of a run. The run's <c>failure_reason</c> holds one line per failure,
/// <c>{agent}: {status}: {detail}</c>, so a resumed run knows which steps did not complete.
/// </summary>
public sealed record AiStepFailure(string Agent, AgentStatus Status, string Detail)
{
    public const int MaxDetailLength = 400;

    public static string Format(IEnumerable<AiStepFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return string.Join('\n', failures.Select(f => $"{f.Agent}: {f.Status}: {OneLine(f.Detail)}"));
    }

    /// <summary>The failures recorded in a <c>failure_reason</c>; lines that are not in the format are ignored.</summary>
    public static IReadOnlyList<AiStepFailure> Parse(string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(failureReason))
        {
            return [];
        }

        var failures = new List<AiStepFailure>();
        foreach (var line in failureReason.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(": ", 3);
            if (parts.Length == 3
                && parts[0] is AgentNames.Intake or AgentNames.Evidence or AgentNames.Policy or AgentNames.Decision
                && Enum.TryParse<AgentStatus>(parts[1], out var status))
            {
                failures.Add(new AiStepFailure(parts[0], status, parts[2]));
            }
        }

        return failures;
    }

    private static string OneLine(string detail)
    {
        var line = string.Join(' ', (detail ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= MaxDetailLength ? line : line[..MaxDetailLength];
    }
}
