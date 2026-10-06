using Warranty.Domain.Policies;

namespace Warranty.Guardrails.Rules;

/// <summary>How the accidental-damage allowance of the applicable version treats the claim.</summary>
public enum AccidentalDamageOutcome
{
    /// <summary>The allowance covers the claim: within the window and below the incident limit.</summary>
    Covered,

    /// <summary>No applicable policy version: the allowance cannot be determined (FR-014).</summary>
    NoApplicablePolicy,

    /// <summary>The version does not cover accidental damage at all (e.g. Tenant A, which excludes it).</summary>
    NotCovered,

    /// <summary>The version covers accidental damage, but the claim date is after the allowance window.</summary>
    OutsideWindow,

    /// <summary>The version covers accidental damage, but the serial already used every allowed incident.</summary>
    IncidentLimitReached,
}

/// <summary>
/// Deterministic accidental-damage decision for one claim (data-model.md "Coverage window rule").
/// </summary>
/// <param name="Outcome">The decision.</param>
/// <param name="WindowEndDate">Last day of the allowance (<c>purchase_date + windowMonths</c>); null when not covered.</param>
/// <param name="WithinWindow">Claim date within the allowance window; null when not covered.</param>
/// <param name="PriorApprovedIncidents">All-time approved accidental-damage claims for the serial (research R25).</param>
/// <param name="MaxIncidents">Incidents the version allows; 0 when not covered.</param>
public sealed record AccidentalDamageResult(
    AccidentalDamageOutcome Outcome,
    DateOnly? WindowEndDate,
    bool? WithinWindow,
    int PriorApprovedIncidents,
    int MaxIncidents)
{
    /// <summary>The allowance covers this claim.</summary>
    public bool IsCovered => Outcome == AccidentalDamageOutcome.Covered;

    /// <summary>The version has an allowance, but this claim is outside it (window ended or limit reached).</summary>
    public bool IsBeyondAllowance => Outcome is AccidentalDamageOutcome.OutsideWindow or AccidentalDamageOutcome.IncidentLimitReached;
}

/// <summary>
/// Decides whether accidental damage is covered (T077): only when the version's
/// <c>accidentalDamage.covered</c> is true, <c>claim_date ≤ purchase_date + windowMonths</c> (inclusive) and
/// the serial's prior approved accidental-damage claims (<c>claim_history_lookup</c>'s
/// <c>priorApprovedAccidental</c>) are fewer than <c>maxIncidents</c>. Reads structured terms and counts only —
/// never clause text or AI output. The allowance is coverage, not an exclusion (research R26).
/// </summary>
public static class AccidentalDamageRule
{
    /// <summary>
    /// The last day of the allowance window; null when <paramref name="terms"/> is null or does not cover
    /// accidental damage (a covered allowance without a positive window is treated as not covered).
    /// </summary>
    public static DateOnly? WindowEndDate(AccidentalDamageTerms? terms, DateOnly purchaseDate)
        => terms is { Covered: true, WindowMonths: > 0 } ? purchaseDate.AddMonths(terms.WindowMonths) : null;

    /// <summary>
    /// Evaluates the allowance. <paramref name="terms"/> is null when no policy version applies.
    /// </summary>
    public static AccidentalDamageResult Evaluate(
        AccidentalDamageTerms? terms, DateOnly purchaseDate, DateOnly claimDate, int priorApprovedAccidental)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(priorApprovedAccidental);
        if (terms is null)
        {
            return new AccidentalDamageResult(AccidentalDamageOutcome.NoApplicablePolicy, null, null, priorApprovedAccidental, 0);
        }

        if (WindowEndDate(terms, purchaseDate) is not { } end)
        {
            return new AccidentalDamageResult(AccidentalDamageOutcome.NotCovered, null, null, priorApprovedAccidental, 0);
        }

        var within = claimDate <= end;
        var outcome = !within
            ? AccidentalDamageOutcome.OutsideWindow
            : priorApprovedAccidental >= terms.MaxIncidents
                ? AccidentalDamageOutcome.IncidentLimitReached
                : AccidentalDamageOutcome.Covered;
        return new AccidentalDamageResult(outcome, end, within, priorApprovedAccidental, terms.MaxIncidents);
    }

    /// <summary>
    /// True when a photo damage type (photo-analysis schema names) shows accidental damage — the same types
    /// <see cref="ExclusionEvidenceMap"/> maps to <c>ACCIDENTAL_DAMAGE</c>.
    /// </summary>
    public static bool IsEvidenced(IEnumerable<string> photoDamageTypes)
        => ExclusionEvidenceMap.IsSupported(ExclusionCode.AccidentalDamage, photoDamageTypes);
}
