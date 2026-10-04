using Warranty.Application.Abstractions.Knowledge;
using Warranty.Domain.Adjudication;
using Warranty.Guardrails.Pipeline;
using Warranty.Guardrails.Rules;

namespace Warranty.AI.Harness.Context;

/// <summary>
/// The Policy step's deterministic facts next to the model's assessment (T063). <see cref="PolicyResult.Outcome"/>
/// is the step's version outcome; <see cref="PolicyResult.Clauses"/> holds every <c>POL-n</c> issued in the
/// run — the retrieved clauses and those the model found with <c>search_policy_knowledge</c> — so the
/// guardrails' <c>REFERENCES_VALID</c> can resolve any cited clause; <see cref="PolicyResult.Version"/> is the
/// applicable version with its structured terms.
/// </summary>
public sealed partial record PolicyResult
{
    /// <summary>No window: no applicable policy version (FR-014).</summary>
    public static CoverageWindowResult NoCoverageWindow { get; } = new(CoverageWindowOutcome.NoApplicablePolicy, null, null, null);

    /// <summary>
    /// The version outcome for <c>adjudication.policy_assessments</c> and the guardrails. A scope violation
    /// left nothing applicable to read, so it counts as <see cref="PolicyVersionOutcome.NoApplicablePolicy"/>.
    /// </summary>
    public PolicyVersionOutcome VersionOutcome => Outcome switch
    {
        RetrievalOutcome.Ok when Version is not null => PolicyVersionOutcome.Ok,
        RetrievalOutcome.AmbiguousPolicyVersion => PolicyVersionOutcome.AmbiguousPolicyVersion,
        _ => PolicyVersionOutcome.NoApplicablePolicy,
    };

    /// <summary>
    /// The deterministic coverage window of the applicable version for the claimed component and the claim
    /// date (<see cref="CoverageWindowCalculator"/>, same rule as <c>warranty_lookup</c>); not determined
    /// without an applicable version.
    /// </summary>
    public CoverageWindowResult CoverageWindow { get; init; } = NoCoverageWindow;

    /// <summary>The intake component the window was computed for (<c>UNKNOWN</c> without an extraction).</summary>
    public string Component { get; init; } = "UNKNOWN";

    /// <summary>The model's <c>coverageAssessment</c> (<c>COVERED</c>, <c>NOT_COVERED</c>, <c>UNDETERMINED</c>); null without a valid assessment.</summary>
    public string? CoverageAssessment { get; init; }

    /// <summary>
    /// The model's own <c>ambiguity.isAmbiguous</c> flag (wording conflicts, no clause covers the case, …).
    /// Not part of <see cref="PolicyFacts"/> yet; the runner can pass it on as a reason for review.
    /// </summary>
    public bool IsAmbiguous { get; init; }

    /// <summary>The model's <c>ambiguity.explanation</c>; null without a valid assessment.</summary>
    public string? AmbiguityExplanation { get; init; }

    /// <summary>The guardrail engine's view of this step (contracts/agents-and-tools.md).</summary>
    public PolicyFacts ToFacts() => new(VersionOutcome, Version, Clauses, CoverageWindow);
}
