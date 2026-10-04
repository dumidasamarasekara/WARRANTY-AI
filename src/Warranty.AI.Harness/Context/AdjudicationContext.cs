using Warranty.AI.Harness.Agents.Risk;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Domain.Policies;

namespace Warranty.AI.Harness.Context;

/// <summary>
/// Execution state of one run (contracts/agents-and-tools.md). The harness passes typed step outputs
/// through it; agents never call each other. The tenant is the job's context and is read-only: no step
/// output or model response can change which tenant the run works for.
/// </summary>
public sealed class AdjudicationContext
{
    public AdjudicationContext(Guid runId, ITenantContext tenant, CaseContext @case, ReferenceRegistry references)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(@case);
        ArgumentNullException.ThrowIfNull(references);
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("A run ID is required.", nameof(runId));
        }

        if (!tenant.IsResolved)
        {
            throw new InvalidOperationException("A run needs the tenant context of its job.");
        }

        RunId = runId;
        Tenant = tenant;
        Case = @case;
        References = references;
    }

    public Guid RunId { get; }

    public Guid ClaimId => Case.ClaimId;

    public int Round => Case.Round;

    /// <summary>From the job record; read-only.</summary>
    public ITenantContext Tenant { get; }

    /// <summary>Claim, product, redacted customer view and evidence descriptors.</summary>
    public CaseContext Case { get; }

    /// <summary>Issues and resolves <c>EV-n</c>, <c>POL-n</c> and <c>GLB-n</c>.</summary>
    public ReferenceRegistry References { get; }

    public IntakeResult? Intake { get; set; }

    public EvidenceResult? Evidence { get; set; }

    /// <summary>Retrieved clauses, assessment and structured terms.</summary>
    public PolicyResult? Policy { get; set; }

    public RiskAssessment? Risk { get; set; }

    public RecommendationResult? Recommendation { get; set; }

    public GuardrailEvaluation? Guardrails { get; set; }
}

/// <summary>Evidence step output: one finding per file plus the deterministic claim-vs-evidence checks (FR-016).</summary>
public sealed record EvidenceResult(
    IReadOnlyList<EvidenceFinding> Findings,
    IReadOnlyList<ConsistencyCheck> ConsistencyChecks,
    IReadOnlyList<RequestedItem> MissingItems);

/// <summary>Policy step output: the version outcome, the issued clauses, the model's assessment and the version's terms.</summary>
public sealed partial record PolicyResult(
    RetrievalOutcome Outcome,
    IReadOnlyList<RetrievedPolicyRef> Clauses,
    PolicyAssessment? Assessment,
    PolicyVersion? Version);

/// <summary>Decision step output: the recommendation, valid or not, and what the model reported about risk.</summary>
/// <param name="Recommendation">The stored recommendation (<c>adjudication.recommendations</c>).</param>
/// <param name="Risk">
/// The decision output's <c>risk.level</c> and <c>risk.signals</c> as AI-sourced signals; <see cref="AiRiskReading.None"/>
/// when the output did not match the schema. The runner adds the evidence-derived AI signals before
/// calling <see cref="IRiskAssessor.AssessFullAsync"/>.
/// </param>
public sealed record RecommendationResult(Recommendation Recommendation, AiRiskReading Risk);
