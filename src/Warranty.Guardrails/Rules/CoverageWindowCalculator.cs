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
    bool? WithinComponentCoverage);

/// <summary>
/// Computes the coverage window from structured <see cref="CoverageTerms"/> only — never from clause
/// text or AI output (T053).
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
        => throw new NotImplementedException("Pending T053.");
}
