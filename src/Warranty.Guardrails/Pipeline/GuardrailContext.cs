using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Guardrails.Rules;

namespace Warranty.Guardrails.Pipeline;

/// <summary>Which part of the run produced the input the guardrails evaluate.</summary>
internal enum EvaluationPath
{
    /// <summary>
    /// Intake found missing items and the AI steps did not run (contracts/agents-and-tools.md). Only the
    /// checks that need no AI output are evaluated: value, category, deterministic risk, loop state
    /// (FR-028 precedence over FR-010).
    /// </summary>
    IntakeShortCircuit,

    /// <summary>The full pipeline ran (or an AI step failed, in which case the recommendation is null).</summary>
    Full,
}

/// <summary>Facts derived once from a <see cref="GuardrailInput"/> and shared by every check.</summary>
internal sealed class GuardrailContext
{
    private readonly Dictionary<string, RetrievedPolicyRef> _clauses = new(StringComparer.Ordinal);

    public GuardrailContext(GuardrailInput input)
    {
        Input = input;
        Path = input.Recommendation is null && input.Intake.MissingItems.Count > 0
            ? EvaluationPath.IntakeShortCircuit
            : EvaluationPath.Full;

        ValidRecommendation = input.Recommendation is { IsValid: true, Decision: not null, Coverage: not null, Confidence: not null } recommendation
            ? recommendation
            : null;

        foreach (var clause in input.Policy?.Clauses ?? [])
        {
            _clauses.TryAdd(clause.RefId, clause);
        }

        RequestedItems = CollectRequestedItems(input, ValidRecommendation);
        RequiredInfoComplete = input.Intake.Validation.All(check => check.Passed) && RequestedItems.Count == 0;
        InformationNeeded = !RequiredInfoComplete || Decision == AiDecision.RequestMoreInformation;
        WithinCoverage = ComputeWithinCoverage(input);
    }

    public GuardrailInput Input { get; }

    public EvaluationPath Path { get; }

    public bool IsShortCircuit => Path == EvaluationPath.IntakeShortCircuit;

    /// <summary>The recommendation when it is valid and carries a decision, coverage and confidence; otherwise null.</summary>
    public Recommendation? ValidRecommendation { get; }

    public AiDecision? Decision => ValidRecommendation?.Decision;

    /// <summary>Why the recommendation cannot be relied on: unavailable (FR-031) or invalid (FR-023).</summary>
    public EscalationReason MissingRecommendationReason
        => Input.Recommendation is null ? EscalationReason.AiUnavailable : EscalationReason.InvalidRecommendation;

    /// <summary>
    /// Items that can be requested from the submitter: intake missing items, then evidence missing items
    /// (e.g. <c>PHOTO_OF_DAMAGE</c>, research R23), then the valid recommendation's missing information;
    /// the first occurrence of each item code wins.
    /// </summary>
    public IReadOnlyList<RequestedItem> RequestedItems { get; }

    /// <summary>Every intake validation check passed and nothing is missing (FR-009, FR-010).</summary>
    public bool RequiredInfoComplete { get; }

    /// <summary>A <see cref="Disposition.RequestInformation"/> would be issued if nothing escalates (FR-010, FR-029).</summary>
    public bool InformationNeeded { get; }

    /// <summary>
    /// The deterministic coverage window's deciding flag (<c>WithinComponentCoverage</c>); null when the
    /// window was not determined or contradicts the claim date and coverage end date (fail closed).
    /// </summary>
    public bool? WithinCoverage { get; }

    /// <summary>A <c>POL-n</c> issued for this run and present among the run's retrieved clauses.</summary>
    public bool TryGetIssuedClause(string reference, out RetrievedPolicyRef clause)
    {
        if (reference is not null
            && reference.StartsWith("POL-", StringComparison.Ordinal)
            && Input.IssuedReferences.Contains(reference)
            && _clauses.TryGetValue(reference, out var found))
        {
            clause = found;
            return true;
        }

        clause = null!;
        return false;
    }

    /// <summary>An <c>EV-n</c> issued for this run.</summary>
    public bool IsIssuedEvidence(string reference)
        => reference is not null
            && reference.StartsWith("EV-", StringComparison.Ordinal)
            && Input.IssuedReferences.Contains(reference);

    private static List<RequestedItem> CollectRequestedItems(GuardrailInput input, Recommendation? recommendation)
    {
        var items = new List<RequestedItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<RequestedItem> sources = input.Intake.MissingItems
            .Concat(input.Evidence?.MissingItems ?? [])
            .Concat(recommendation?.MissingInformation ?? []);
        foreach (var item in sources)
        {
            if (item is not null && seen.Add(item.Item))
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static bool? ComputeWithinCoverage(GuardrailInput input)
    {
        var window = input.Policy?.CoverageWindow;
        if (window is not { Outcome: CoverageWindowOutcome.Determined, WithinComponentCoverage: { } within, CoverageEndDate: { } end })
        {
            return null;
        }

        return (input.ClaimDate <= end) == within ? within : null;
    }
}
