using Warranty.Domain.Fulfillment;

namespace Warranty.Domain.Policies;

/// <summary>
/// Structured remedy rules of one policy version (data-model.md <c>remedy_rules</c> JSON, research R1).
/// Read only by the deterministic remedy evaluator, never from clause text. Every row is optional; a
/// missing row gives no remedy for that scenario and sends the remedy to manual review (FR-012).
/// </summary>
public sealed record RemedyRules(
    RemedyChoice? CoveredDefect,
    RepairNotEconomicalRule? RepairNotEconomical,
    RepairFailedRule? RepairFailed,
    ProtectionPlanRule? ProtectionPlan)
{
    /// <summary>Throws when a row is inconsistent (same rules as contracts/remedy-rules.schema.json).</summary>
    public void Validate()
    {
        CoveredDefect?.Validate(nameof(CoveredDefect));
        RepairNotEconomical?.Validate();
        RepairFailed?.Validate();
        ProtectionPlan?.Validate();
    }
}

/// <summary>
/// One remedy row: exactly one of <see cref="Remedy"/> (set automatically) or <see cref="Allowed"/>
/// (two or three distinct remedies, a reviewer chooses; FR-003).
/// </summary>
public record RemedyChoice(RemedyType? Remedy, IReadOnlyList<RemedyType>? Allowed)
{
    public const int MinAllowed = 2;
    public const int MaxAllowed = 3;

    /// <summary>The remedies this row permits: the single remedy, or the allowed list.</summary>
    public IReadOnlyList<RemedyType> AllowedRemedies
        => Remedy is { } remedy ? [remedy] : Allowed ?? [];

    /// <summary>True when a reviewer must choose between several allowed remedies.</summary>
    public bool NeedsReviewer => Remedy is null && Allowed is { Count: > 1 };

    internal void Validate(string row)
    {
        if (Remedy is not null && Allowed is not null)
        {
            throw new ArgumentException($"Remedy row '{row}' must hold either a remedy or an allowed list, not both.");
        }

        if (Remedy is null && Allowed is null)
        {
            throw new ArgumentException($"Remedy row '{row}' must hold a remedy or an allowed list.");
        }

        if (Remedy is { } remedy && !Enum.IsDefined(remedy))
        {
            throw new ArgumentException($"Remedy row '{row}' has an unknown remedy '{remedy}'.");
        }

        if (Allowed is { } allowed)
        {
            if (allowed.Count is < MinAllowed or > MaxAllowed)
            {
                throw new ArgumentException(
                    $"Remedy row '{row}' must allow between {MinAllowed} and {MaxAllowed} remedies.");
            }

            if (allowed.Any(r => !Enum.IsDefined(r)))
            {
                throw new ArgumentException($"Remedy row '{row}' allows an unknown remedy.");
            }

            if (allowed.Distinct().Count() != allowed.Count)
            {
                throw new ArgumentException($"Remedy row '{row}' must not allow the same remedy twice.");
            }
        }
    }
}

/// <summary>Applies when the repair estimate is strictly above the limit; an equal estimate is economical (FR-004).</summary>
public sealed record RepairNotEconomicalRule(
    int LimitPercentOfClaimValue,
    RemedyType? Remedy,
    IReadOnlyList<RemedyType>? Allowed) : RemedyChoice(Remedy, Allowed)
{
    public const int MinLimitPercent = 1;
    public const int MaxLimitPercent = 100;

    internal void Validate()
    {
        if (LimitPercentOfClaimValue is < MinLimitPercent or > MaxLimitPercent)
        {
            throw new ArgumentException(
                $"The repair-not-economical limit must be {MinLimitPercent}–{MaxLimitPercent} percent of the claim value.");
        }

        Validate(nameof(RemedyRules.RepairNotEconomical));
    }
}

/// <summary>Applies when the failed repair attempts reach <see cref="AfterFailedAttempts"/> (FR-005).</summary>
public sealed record RepairFailedRule(
    int AfterFailedAttempts,
    RemedyType? Remedy,
    IReadOnlyList<RemedyType>? Allowed) : RemedyChoice(Remedy, Allowed)
{
    public const int MinFailedAttempts = 1;
    public const int MaxFailedAttempts = 5;

    internal void Validate()
    {
        if (AfterFailedAttempts is < MinFailedAttempts or > MaxFailedAttempts)
        {
            throw new ArgumentException(
                $"The repair-failed attempt limit must be {MinFailedAttempts}–{MaxFailedAttempts}.");
        }

        Validate(nameof(RemedyRules.RepairFailed));
    }
}

/// <summary>
/// Whether a confirmed protection plan lifts the accidental-damage exclusion and, if so, the remedy
/// then (FR-006). A plan that does not lift the exclusion carries no remedy.
/// </summary>
public sealed record ProtectionPlanRule(
    bool LiftsAccidentalDamageExclusion,
    RemedyType? Remedy,
    IReadOnlyList<RemedyType>? Allowed) : RemedyChoice(Remedy, Allowed)
{
    internal void Validate()
    {
        if (LiftsAccidentalDamageExclusion)
        {
            Validate(nameof(RemedyRules.ProtectionPlan));
        }
        else if (Remedy is not null || Allowed is not null)
        {
            throw new ArgumentException(
                "A protection plan that does not lift the accidental-damage exclusion must not name a remedy.");
        }
    }
}
