using Warranty.AI.Harness.Context;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Guardrails.Rules;

namespace Warranty.AI.Harness.Agents;

/// <summary>
/// Everything the Decision Agent reads (contracts/agents-and-tools.md: "All of the above"), as Domain
/// records rather than other agents' result types, so the runner maps the step outputs in one place.
/// </summary>
/// <param name="Case">The run's case; must be the run's claim.</param>
/// <param name="Intake">The intake result (validation, missing items, extraction).</param>
/// <param name="EvidenceFindings">One finding per analysed evidence file; empty when the Evidence step produced none.</param>
/// <param name="EvidenceMissingItems">Items the Evidence step asks for (e.g. a photo that shows neither product nor damage).</param>
/// <param name="PolicyVersionOutcome">Deterministic policy version selection; null when the Policy step did not get that far.</param>
/// <param name="PolicyAssessment">The Policy Agent's assessment; null when it failed.</param>
/// <param name="PolicyClauses">Every <c>POL-n</c> issued for the run, with clause type and score.</param>
/// <param name="CoverageWindow">The deterministic coverage window (<see cref="CoverageWindowCalculator"/>).</param>
/// <param name="DeterministicSignals">From <c>IRiskAssessor.DetectDeterministicSignalsAsync</c>; the model is asked to repeat them in <c>risk.signals</c>.</param>
public sealed record DecisionInput(
    CaseContext Case,
    IntakeResult Intake,
    IReadOnlyList<EvidenceFinding> EvidenceFindings,
    IReadOnlyList<RequestedItem> EvidenceMissingItems,
    PolicyVersionOutcome? PolicyVersionOutcome,
    PolicyAssessment? PolicyAssessment,
    IReadOnlyList<RetrievedPolicyRef> PolicyClauses,
    CoverageWindowResult CoverageWindow,
    IReadOnlyList<RiskSignal> DeterministicSignals)
{
    /// <summary>
    /// The input from the run state: case, intake, evidence and policy outputs as stored on
    /// <paramref name="run"/>, plus the two deterministic results the runner computes before the step.
    /// </summary>
    public static DecisionInput From(AdjudicationContext run, CoverageWindowResult coverageWindow, IReadOnlyList<RiskSignal> deterministicSignals)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(coverageWindow);
        ArgumentNullException.ThrowIfNull(deterministicSignals);
        var intake = run.Intake ?? throw new InvalidOperationException("The Decision step needs the intake result.");

        return new DecisionInput(
            run.Case,
            intake,
            run.Evidence?.Findings ?? [],
            run.Evidence?.MissingItems ?? [],
            run.Policy?.Assessment?.VersionOutcome ?? VersionOutcomeOf(run.Policy?.Outcome),
            run.Policy?.Assessment,
            run.Policy?.Clauses ?? [],
            coverageWindow,
            deterministicSignals);
    }

    private static PolicyVersionOutcome? VersionOutcomeOf(RetrievalOutcome? outcome) => outcome switch
    {
        RetrievalOutcome.Ok => Domain.Adjudication.PolicyVersionOutcome.Ok,
        RetrievalOutcome.NoApplicablePolicy => Domain.Adjudication.PolicyVersionOutcome.NoApplicablePolicy,
        RetrievalOutcome.AmbiguousPolicyVersion => Domain.Adjudication.PolicyVersionOutcome.AmbiguousPolicyVersion,
        _ => null,
    };
}
