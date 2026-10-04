using System.Text.Json;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Agents.Risk;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Rules;

namespace Warranty.AI.Harness.Execution;

public sealed partial class AdjudicationRunner
{
    /// <summary>
    /// The execution state of a new run, or of a resumed run rebuilt from what its completed steps stored:
    /// the reference map, the intake result, the evidence findings, the policy step (clauses, assessment,
    /// version and coverage window), and once the Decision step committed, the recommendation and risk.
    /// </summary>
    private async Task<RunState> RestoreAsync(AdjudicationRun run, CaseContext @case, CancellationToken ct)
    {
        var references = ReferenceRegistry.FromMap(run.ReferenceMap);
        var ctx = new AdjudicationContext(run.Id, tenant, @case, references);
        var state = new RunState(run, ctx, AiStepFailure.Parse(run.FailureReason));
        if (run.CurrentStep == RunStep.Intake)
        {
            return state;
        }

        var record = await adjudication.GetRunRecordAsync(run.Id, ct)
                     ?? throw new InvalidOperationException($"Run {run.Id} cannot be read back to resume it.");
        var intake = record.Intake ?? throw new InvalidOperationException($"Run {run.Id} is past intake but has no intake result.");
        ctx.Intake = intake;
        state.ShortCircuit = intake.MissingItems.Count > 0;

        var analysed = !state.ShortCircuit && !state.Failed(AgentNames.Intake);
        if (analysed && run.CurrentStep > RunStep.Evidence)
        {
            ctx.Evidence = EvidenceAgent.Restore(record.EvidenceFindings, references);
        }

        if (analysed && run.CurrentStep > RunStep.Policy && record.PolicyAssessment is { } assessment)
        {
            ctx.Policy = await RestorePolicyAsync(ctx, assessment, record.PolicyReferences, ct);
        }

        if (run.CurrentStep > RunStep.Decision || (state.ShortCircuit && run.CurrentStep > RunStep.Risk))
        {
            ctx.Risk = record.Risk;
            ctx.Recommendation = record.Recommendation is { } recommendation ? new RecommendationResult(recommendation, AiRiskReading.None) : null;
        }

        ctx.Guardrails = record.Guardrails;
        return state;
    }

    /// <summary>
    /// The Policy step output from its stored assessment and clauses. The version comes from the clauses, the
    /// window is recomputed with <see cref="CoverageWindowCalculator"/> exactly as the Policy Agent computed
    /// it, and each <c>POL-n</c> gets its clause wording back so the Decision step can read it.
    /// </summary>
    private async Task<PolicyResult> RestorePolicyAsync(
        AdjudicationContext ctx, PolicyAssessment assessment, IReadOnlyList<RetrievedPolicyRef> clauses, CancellationToken ct)
    {
        PolicyVersion? version = null;
        if (assessment.VersionOutcome == PolicyVersionOutcome.Ok && clauses.Count > 0)
        {
            version = await policies.GetVersionAsync(clauses[0].PolicyVersionId, ct);
        }

        var outcome = assessment.VersionOutcome switch
        {
            PolicyVersionOutcome.Ok when version is not null => RetrievalOutcome.Ok,
            PolicyVersionOutcome.AmbiguousPolicyVersion => RetrievalOutcome.AmbiguousPolicyVersion,
            _ => RetrievalOutcome.NoApplicablePolicy,
        };

        var component = PolicyAgent.ComponentOf(ctx.Intake is { } intake ? IntakeExtraction.From(intake) : null);
        var window = version is not null && ctx.Case.Region is { } region
            ? CoverageWindowCalculator.Calculate(version.Terms, region, WarrantyLookupTool.ComponentKey(component), ctx.Case.PurchaseDate, ctx.Case.ClaimDate)
            : PolicyResult.NoCoverageWindow;

        if (version is not null)
        {
            var wording = (await policies.GetClausesAsync(version.Id, ct)).ToDictionary(c => c.ClauseKey, StringComparer.Ordinal);
            foreach (var clause in clauses)
            {
                if (wording.TryGetValue(clause.ClauseKey, out var text))
                {
                    ctx.References.IssueChunk(new RetrievedChunk(
                        clause.KnowledgeChunkId, tenant.KnowledgeNamespace, Guid.Empty, clause.DocumentTitle, clause.Version, clause.ClauseKey,
                        text.Title, text.Text, clause.EffectiveFrom, clause.EffectiveTo, clause.Score, clause.TenantId, clause.PolicyVersionId,
                        clause.ClauseType, clause.ExclusionCode));
                }
            }
        }

        var reading = AssessmentReading.Parse(assessment.AssessmentJson);
        return new PolicyResult(outcome, clauses, assessment, version)
        {
            CoverageWindow = window,
            Component = component,
            CoverageAssessment = reading.CoverageAssessment,
            IsAmbiguous = reading.IsAmbiguous,
            AmbiguityExplanation = reading.AmbiguityExplanation,
        };
    }

    /// <summary>The fields of a stored policy assessment the runner passes on; empty for <c>{}</c>.</summary>
    private sealed record AssessmentReading(string? CoverageAssessment, bool IsAmbiguous, string? AmbiguityExplanation)
    {
        public static AssessmentReading Parse(string? json)
        {
            try
            {
                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return new(null, false, null);
                }

                var coverage = root.TryGetProperty("coverageAssessment", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var ambiguous = false;
                string? explanation = null;
                if (root.TryGetProperty("ambiguity", out var a) && a.ValueKind == JsonValueKind.Object)
                {
                    ambiguous = a.TryGetProperty("isAmbiguous", out var flag) && flag.ValueKind == JsonValueKind.True;
                    explanation = a.TryGetProperty("explanation", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                }

                return new(coverage, ambiguous, explanation);
            }
            catch (JsonException)
            {
                return new(null, false, null);
            }
        }
    }
}
