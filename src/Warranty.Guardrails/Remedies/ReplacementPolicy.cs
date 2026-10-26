using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;

namespace Warranty.Guardrails.Remedies;

/// <summary>
/// The deterministic replacement rules (003 research R2): a pure function of the policy version's
/// <see cref="ReplacementRules"/> and the facts given. Every company runs the same code; only the rules differ (FR-006).
/// </summary>
/// <remarks>
/// A policy version without replacement rules gets <see cref="ReplacementRules.Default"/>: same model only, faulty
/// product returned at collection (spec.md edge cases).
/// </remarks>
public sealed class ReplacementPolicy
{
    /// <summary>
    /// The status a new replacement starts in (FR-013, research R9): <see cref="ReplacementStatus.AwaitingReturn"/>
    /// only under <see cref="ReplacementReturnRule.BeforeReservation"/> when the faulty product is not already held by
    /// a repair centre; otherwise <see cref="ReplacementStatus.AwaitingUnit"/>. The return state is not decided here:
    /// a held product is recorded by the executor's <c>System</c> <see cref="ReplacementEventType.ReturnRecorded"/>.
    /// </summary>
    /// <param name="rules">The applicable policy version's replacement rules; null when the version has none.</param>
    /// <param name="productHeldByRepairCentre">True when the claim's 002 repair holds the faulty product (research R9).</param>
    public ReplacementStatus InitialStatus(ReplacementRules? rules, bool productHeldByRepairCentre)
        => (rules ?? ReplacementRules.Default).ReturnRule == ReplacementReturnRule.BeforeReservation && !productHeldByRepairCentre
            ? ReplacementStatus.AwaitingReturn
            : ReplacementStatus.AwaitingUnit;

    /// <summary>
    /// Whether <paramref name="proposed"/> may be handed out instead of the claimed <paramref name="original"/>
    /// (FR-002, FR-023): the same model is always allowed; another model only under
    /// <see cref="ReplacementProductRule.SameCategoryAtLeastClaimValue"/>, of the same category and with a claim value
    /// at least the original's (equal is allowed), and then only after a reviewer approves it. A missing claim value on
    /// either side cannot be compared and is refused. Model codes and categories compare case-insensitively.
    /// </summary>
    /// <param name="rules">The applicable policy version's replacement rules; null when the version has none.</param>
    /// <param name="original">The claimed (faulty) product model.</param>
    /// <param name="proposed">The model staff propose to hand out.</param>
    public ModelCheckResult CheckModel(ReplacementRules? rules, ReplacementModel original, ReplacementModel proposed)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(proposed);

        if (string.Equals(original.ModelCode, proposed.ModelCode, StringComparison.OrdinalIgnoreCase))
        {
            return new ModelCheckResult(ReplacementModelVerdict.SameModel, null);
        }

        if ((rules ?? ReplacementRules.Default).ProductRule != ReplacementProductRule.SameCategoryAtLeastClaimValue)
        {
            return Refused(ReplacementModelRefusal.SameModelOnly);
        }

        if (!string.Equals(original.Category, proposed.Category, StringComparison.OrdinalIgnoreCase))
        {
            return Refused(ReplacementModelRefusal.OtherCategory);
        }

        if (original.ClaimValue is not { } originalValue || proposed.ClaimValue is not { } proposedValue)
        {
            return Refused(ReplacementModelRefusal.MissingClaimValue);
        }

        return proposedValue >= originalValue
            ? new ModelCheckResult(ReplacementModelVerdict.NeedsReviewerApproval, null)
            : Refused(ReplacementModelRefusal.LowerClaimValue);
    }

    private static ModelCheckResult Refused(ReplacementModelRefusal refusal) => new(ReplacementModelVerdict.Refused, refusal);
}

/// <summary>A product model as the replacement rules compare it; <see cref="ClaimValue"/> is null when the catalog has none.</summary>
public sealed record ReplacementModel(string ModelCode, string Category, decimal? ClaimValue);

/// <summary>Outcome of <see cref="ReplacementPolicy.CheckModel"/>.</summary>
public enum ReplacementModelVerdict
{
    /// <summary>The claimed model itself: no approval needed.</summary>
    SameModel,

    /// <summary>An allowed equivalent: a reviewer must approve it before a unit is reserved (FR-024).</summary>
    NeedsReviewerApproval,

    /// <summary>Not allowed; <see cref="ModelCheckResult.Refusal"/> names the rule.</summary>
    Refused,
}

/// <summary>Why a proposed model is refused (FR-023; the message names the rule of the policy).</summary>
public enum ReplacementModelRefusal
{
    /// <summary>The policy's product rule is <see cref="ReplacementProductRule.SameModel"/>.</summary>
    SameModelOnly,

    /// <summary>The proposed model belongs to another product category.</summary>
    OtherCategory,

    /// <summary>The proposed model's claim value is below the original's.</summary>
    LowerClaimValue,

    /// <summary>The original or the proposed model has no claim value, so they cannot be compared.</summary>
    MissingClaimValue,
}

/// <param name="Verdict">Whether the model is the same, needs a reviewer, or is refused.</param>
/// <param name="Refusal">Why it is refused; null unless <see cref="Verdict"/> is <see cref="ReplacementModelVerdict.Refused"/>.</param>
public sealed record ModelCheckResult(ReplacementModelVerdict Verdict, ReplacementModelRefusal? Refusal)
{
    /// <summary>True when the model may be reserved, possibly after a reviewer's approval.</summary>
    public bool Allowed => Verdict != ReplacementModelVerdict.Refused;
}
