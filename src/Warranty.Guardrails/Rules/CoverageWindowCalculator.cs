using Warranty.Domain.Common;
using Warranty.Domain.Policies;

namespace Warranty.Guardrails.Rules;

/// <summary>Whether a coverage window could be computed for the claim.</summary>
public enum CoverageWindowOutcome
{
    /// <summary>Terms of an applicable policy version were available and the window was computed.</summary>
    Determined,

    /// <summary>No applicable policy version (or no terms for the claim's region): coverage is undetermined (FR-014).</summary>
    NoApplicablePolicy,
}

/// <summary>
/// Deterministic coverage window of one claim (data-model.md "Coverage window rule").
/// <see cref="CoverageEndDate"/> is <c>purchase_date + months</c>, using the component-specific months
/// when the terms list the claimed component and the standard months for the region otherwise; the
/// claim is covered iff <c>claim_date ≤ CoverageEndDate</c> (inclusive).
/// </summary>
/// <param name="Outcome">Whether the window was determined.</param>
/// <param name="CoverageEndDate">Last covered day for the claimed component; null when not determined.</param>
/// <param name="WithinStandardCoverage">Claim date within the region's standard window; null when not determined.</param>
/// <param name="WithinComponentCoverage">
/// Claim date within the window that applies to the claimed component (its specific window, else the
/// standard one), i.e. the deciding flag; null when not determined.
/// </param>
public sealed record CoverageWindowResult(
    CoverageWindowOutcome Outcome,
    DateOnly? CoverageEndDate,
    bool? WithinStandardCoverage,
    bool? WithinComponentCoverage)
{
    /// <summary>
    /// Last day of the accidental-damage allowance (<c>purchase_date + accidentalDamage.windowMonths</c>,
    /// <see cref="AccidentalDamageRule"/>); null when the terms do not cover accidental damage or the window
    /// was not determined.
    /// </summary>
    public DateOnly? AccidentalWindowEndDate { get; init; }

    /// <summary>Claim date within the accidental-damage allowance window; null when <see cref="AccidentalWindowEndDate"/> is null.</summary>
    public bool? WithinAccidentalWindow { get; init; }
}

/// <summary>
/// Computes the coverage window from structured <see cref="CoverageTerms"/> only — never from clause
/// text or AI output (T053), including the accidental-damage allowance window (T077); whether an
/// accidental-damage claim is covered also needs the serial's incident count and is decided by
/// <see cref="AccidentalDamageRule"/>.
/// </summary>
public static class CoverageWindowCalculator
{
    /// <summary>
    /// Computes the window for a claim. <paramref name="terms"/> is null when no policy version applies
    /// (<c>NoApplicablePolicy</c>). <paramref name="component"/> is matched case-insensitively against
    /// <see cref="CoverageTerms.ComponentCoverageMonths"/> (e.g. intake's <c>BATTERY</c> → <c>battery</c>);
    /// null or an unlisted component uses the standard months for <paramref name="region"/>.
    /// </summary>
    public static CoverageWindowResult Calculate(
        CoverageTerms? terms,
        Region region,
        string? component,
        DateOnly purchaseDate,
        DateOnly claimDate)
    {
        if (terms is null || !terms.StandardCoverageMonths.TryGetValue(region, out var standardMonths))
        {
            return Undetermined;
        }

        var standardEndDate = purchaseDate.AddMonths(standardMonths);
        var coverageEndDate = TryGetComponentMonths(terms, component, out var componentMonths)
            ? purchaseDate.AddMonths(componentMonths)
            : standardEndDate;

        var accidentalEndDate = AccidentalDamageRule.WindowEndDate(terms.AccidentalDamage, purchaseDate);
        return new CoverageWindowResult(
            CoverageWindowOutcome.Determined,
            coverageEndDate,
            WithinStandardCoverage: claimDate <= standardEndDate,
            WithinComponentCoverage: claimDate <= coverageEndDate)
        {
            AccidentalWindowEndDate = accidentalEndDate,
            WithinAccidentalWindow = accidentalEndDate is { } end ? claimDate <= end : null,
        };
    }

    private static readonly CoverageWindowResult Undetermined =
        new(CoverageWindowOutcome.NoApplicablePolicy, null, null, null);

    private static bool TryGetComponentMonths(CoverageTerms terms, string? component, out int months)
    {
        months = 0;
        var key = component?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        foreach (var (name, value) in terms.ComponentCoverageMonths)
        {
            if (string.Equals(name.Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                months = value;
                return true;
            }
        }

        return false;
    }
}
