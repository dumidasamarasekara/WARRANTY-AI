using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Domain.Policies;
using Warranty.Domain.Tenancy;
using Warranty.Guardrails.Rules;

namespace Warranty.Guardrails.Pipeline;

/// <summary>
/// Everything the guardrail engine evaluates for one run (contracts/agents-and-tools.md). The harness
/// maps its step outputs into these types, because <c>Warranty.Guardrails</c> references no AI project.
/// <see cref="Recommendation"/> and <see cref="Policy"/> are null when the AI step did not complete
/// (FR-031) or the run short-circuited at intake.
/// </summary>
public sealed record GuardrailInput(
    TenantSettings Settings,
    CaseFacts Case,
    IntakeResult Intake,
    EvidenceFacts? Evidence,
    PolicyFacts? Policy,
    RiskAssessment? Risk,
    Recommendation? Recommendation,
    IReadOnlySet<string> IssuedReferences,
    ActorInfo Actor,
    DateOnly ClaimDate);

/// <summary>
/// Deterministic facts about the claim, including its loop state (research R24). The claim value is
/// the catalog value of the product model; it and the category are null when the product is not in
/// the tenant's catalog.
/// </summary>
public sealed record CaseFacts(
    Guid TenantId,
    Guid ClaimId,
    Guid RunId,
    bool ProductInCatalog,
    string? ProductCategory,
    decimal? ClaimValue,
    bool ReviewerInfoRequested,
    int AutoInfoRequestCount);

/// <summary>Evidence step facts: claim-vs-evidence consistency checks (FR-016), photo findings and missing items.</summary>
public sealed record EvidenceFacts(
    IReadOnlyList<ConsistencyCheck> ConsistencyChecks,
    IReadOnlyList<PhotoFinding> Photos,
    IReadOnlyList<RequestedItem> MissingItems);

/// <summary>One photo analysis; damage types are the photo-analysis schema's names, e.g. <c>CRACKED_SCREEN</c>.</summary>
public sealed record PhotoFinding(string EvidenceRef, IReadOnlyList<string> DamageTypes);

/// <summary>
/// Policy step facts: the version outcome, the applicable version (with its structured terms), the
/// issued <c>POL-n</c> clauses and the deterministic coverage window for the claim's region and
/// component, computed by <see cref="CoverageWindowCalculator"/> (its <c>WithinComponentCoverage</c>
/// is the deciding flag).
/// </summary>
public sealed record PolicyFacts(
    PolicyVersionOutcome VersionOutcome,
    PolicyVersion? Version,
    IReadOnlyList<RetrievedPolicyRef> Clauses,
    CoverageWindowResult CoverageWindow);

/// <summary>Who triggered the evaluation; automatic runs are performed by the adjudication worker.</summary>
public sealed record ActorInfo(string Subject, bool IsAutomation)
{
    public static ActorInfo Automation { get; } = new("adjudication-worker", true);
}
