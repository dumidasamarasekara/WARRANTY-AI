using Warranty.Domain.Claims;

namespace Warranty.Domain.Adjudication;

/// <summary>Evidence relied upon, resolved from a harness-issued <c>EV-n</c>.</summary>
public sealed record EvidenceCitation(string Ref, string Observation);

/// <summary>Policy clause relied upon, resolved from a harness-issued <c>POL-n</c>.</summary>
public sealed record PolicyCitation(string Ref, PolicyRefRelevance Relevance);

/// <summary>
/// The Decision Agent's structured recommendation for a run (FR-020, FR-021). It is advisory only,
/// is validated by the guardrails, and is never modified after creation (FR-036): every property is
/// read-only and there is no mutating method.
/// </summary>
public sealed class Recommendation
{
    private Recommendation()
    {
        RawOutputJson = ReasoningSummary = ClaimantExplanation = Model = PromptId = PromptVersion = string.Empty;
        ValidationErrors = [];
        EvidenceRefs = [];
        PolicyRefs = [];
        MissingInformation = [];
    }

    public Guid RunId { get; private init; }

    public Guid TenantId { get; private init; }

    /// <summary>Model output after redaction, exactly as received.</summary>
    public string RawOutputJson { get; private init; }

    /// <summary>Schema and reference validation result; an invalid recommendation routes to review (FR-023).</summary>
    public bool IsValid { get; private init; }

    public IReadOnlyList<string> ValidationErrors { get; private init; }

    /// <summary>Null when the output was too malformed to read a decision.</summary>
    public AiDecision? Decision { get; private init; }

    public CoverageDetermination? Coverage { get; private init; }

    /// <summary>Model-reported confidence 0–100, used as-is in the PoC (plan: PoC simplifications).</summary>
    public int? Confidence { get; private init; }

    /// <summary>Staff-facing reasoning summary.</summary>
    public string ReasoningSummary { get; private init; }

    /// <summary>Claimant-facing explanation; screened by CLAIMANT_TEXT_SAFE before any use (research R25).</summary>
    public string ClaimantExplanation { get; private init; }

    public IReadOnlyList<EvidenceCitation> EvidenceRefs { get; private init; }

    public IReadOnlyList<PolicyCitation> PolicyRefs { get; private init; }

    public IReadOnlyList<RequestedItem> MissingInformation { get; private init; }

    public bool ManipulationDetected { get; private init; }

    public string Model { get; private init; }

    public string PromptId { get; private init; }

    public string PromptVersion { get; private init; }

    /// <summary>Whether a reviewer's decision would override this recommendation (FR-035).</summary>
    public bool IsOverriddenBy(ReviewDecisionKind decision)
        => IsValid && Decision switch
        {
            AiDecision.Approve => decision != ReviewDecisionKind.Approve,
            AiDecision.Reject => decision != ReviewDecisionKind.Reject,
            _ => false,
        };

    public static Recommendation CreateValid(
        Guid runId, Guid tenantId, string rawOutputJson, AiDecision decision, CoverageDetermination coverage, int confidence,
        string reasoningSummary, string claimantExplanation, IEnumerable<EvidenceCitation> evidenceRefs,
        IEnumerable<PolicyCitation> policyRefs, IEnumerable<RequestedItem> missingInformation, bool manipulationDetected,
        string model, string promptId, string promptVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(confidence);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(confidence, 100);

        return new Recommendation
        {
            RunId = runId,
            TenantId = tenantId,
            RawOutputJson = rawOutputJson,
            IsValid = true,
            Decision = decision,
            Coverage = coverage,
            Confidence = confidence,
            ReasoningSummary = reasoningSummary,
            ClaimantExplanation = claimantExplanation,
            EvidenceRefs = evidenceRefs.ToArray(),
            PolicyRefs = policyRefs.ToArray(),
            MissingInformation = missingInformation.ToArray(),
            ManipulationDetected = manipulationDetected,
            Model = model,
            PromptId = promptId,
            PromptVersion = promptVersion,
        };
    }

    /// <summary>Records output that failed schema or reference validation; it can never finalize a claim.</summary>
    public static Recommendation CreateInvalid(
        Guid runId, Guid tenantId, string rawOutputJson, IEnumerable<string> validationErrors, string model, string promptId,
        string promptVersion, AiDecision? decision = null, int? confidence = null)
    {
        var errors = validationErrors.ToArray();
        if (errors.Length == 0)
        {
            throw new ArgumentException("An invalid recommendation must list its validation errors.", nameof(validationErrors));
        }

        return new Recommendation
        {
            RunId = runId,
            TenantId = tenantId,
            RawOutputJson = rawOutputJson ?? string.Empty,
            IsValid = false,
            ValidationErrors = errors,
            Decision = decision,
            Confidence = confidence,
            Model = model,
            PromptId = promptId,
            PromptVersion = promptVersion,
        };
    }
}
