using Warranty.Domain.Common;

namespace Warranty.Domain.Policies;

/// <summary>
/// Structured coverage terms of one policy version (data-model.md <c>terms</c> JSON). Guardrails
/// compute coverage windows and exclusion grounding from these values, never from clause text.
/// </summary>
public sealed record CoverageTerms(
    IReadOnlyDictionary<Region, int> StandardCoverageMonths,
    IReadOnlyDictionary<string, int> ComponentCoverageMonths,
    AccidentalDamageTerms AccidentalDamage,
    IReadOnlyList<ExclusionCode> Exclusions)
{
    /// <summary>Throws when the terms are inconsistent or do not cover every region of the version.</summary>
    public void Validate(IEnumerable<Region> versionRegions)
    {
        ArgumentNullException.ThrowIfNull(versionRegions);
        foreach (var region in versionRegions)
        {
            if (!StandardCoverageMonths.TryGetValue(region, out var months) || months <= 0)
            {
                throw new ArgumentException($"Standard coverage months must be positive for region {region}.");
            }
        }

        foreach (var (component, months) in ComponentCoverageMonths)
        {
            if (string.IsNullOrWhiteSpace(component) || component != component.Trim().ToLowerInvariant())
            {
                throw new ArgumentException($"Component name '{component}' must be lower-case and trimmed.");
            }

            if (months <= 0)
            {
                throw new ArgumentException($"Coverage months for component '{component}' must be positive.");
            }
        }

        if (Exclusions.Distinct().Count() != Exclusions.Count)
        {
            throw new ArgumentException("Exclusions must not contain duplicates.");
        }

        AccidentalDamage.Validate();
        if (AccidentalDamage.Covered && Exclusions.Contains(ExclusionCode.AccidentalDamage))
        {
            throw new ArgumentException("Accidental damage cannot be both covered and excluded.");
        }
    }

    public bool Excludes(ExclusionCode code) => Exclusions.Contains(code);
}

/// <summary>Accidental-damage allowance; Tenant B covers one incident within the first 12 months.</summary>
public sealed record AccidentalDamageTerms(bool Covered, int WindowMonths, int MaxIncidents)
{
    public static AccidentalDamageTerms NotCovered { get; } = new(false, 0, 0);

    public void Validate()
    {
        if (Covered && (WindowMonths <= 0 || MaxIncidents <= 0))
        {
            throw new ArgumentException("Covered accidental damage needs a positive window and incident limit.");
        }

        if (!Covered && (WindowMonths != 0 || MaxIncidents != 0))
        {
            throw new ArgumentException("Accidental damage that is not covered must have no window or incident limit.");
        }
    }
}
