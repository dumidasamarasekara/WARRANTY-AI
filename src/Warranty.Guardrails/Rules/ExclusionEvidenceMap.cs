using System.Collections.Frozen;
using Warranty.Domain.Policies;

namespace Warranty.Guardrails.Rules;

/// <summary>
/// Fixed mapping from photo-analysis damage types to the exclusion they evidence (research R26):
/// <c>CRACKED_SCREEN</c>, <c>DENTS_OR_IMPACT</c> → <c>ACCIDENTAL_DAMAGE</c>; <c>LIQUID_INDICATORS</c>,
/// <c>CORROSION</c> → <c>LIQUID_DAMAGE</c>; <c>COSMETIC_WEAR</c> → <c>COSMETIC_DAMAGE</c>.
/// <c>UNAUTHORIZED_REPAIR</c> has no photo evidence type. Used by the <c>GROUNDED_IN_CLAUSE</c> check.
/// </summary>
/// <remarks>
/// Damage types are matched ordinally (exact, case-sensitive) against the upper-snake-case names of
/// the <c>damage_types</c> enum in <c>photo-analysis.schema.json</c>. Photo output is schema-validated
/// before it reaches the guardrails, so any other spelling is not a schema value and is deliberately
/// not treated as evidence.
/// </remarks>
public static class ExclusionEvidenceMap
{
    private static readonly FrozenDictionary<ExclusionCode, FrozenSet<string>> EvidenceTypes =
        new Dictionary<ExclusionCode, FrozenSet<string>>
        {
            [ExclusionCode.AccidentalDamage] = Set("CRACKED_SCREEN", "DENTS_OR_IMPACT"),
            [ExclusionCode.LiquidDamage] = Set("LIQUID_INDICATORS", "CORROSION"),
            [ExclusionCode.CosmeticDamage] = Set("COSMETIC_WEAR"),
            [ExclusionCode.UnauthorizedRepair] = Set(),
        }.ToFrozenDictionary();

    /// <summary>
    /// The photo damage types (schema names) that evidence <paramref name="exclusionCode"/>; empty for
    /// <see cref="ExclusionCode.UnauthorizedRepair"/> and for any code without a mapping.
    /// </summary>
    public static IReadOnlySet<string> EvidenceTypesFor(ExclusionCode exclusionCode)
        => EvidenceTypes.TryGetValue(exclusionCode, out var types) ? types : FrozenSet<string>.Empty;

    /// <summary>
    /// True when at least one photo damage type (photo-analysis schema names) maps to
    /// <paramref name="exclusionCode"/>; always false for <see cref="ExclusionCode.UnauthorizedRepair"/>.
    /// </summary>
    public static bool IsSupported(ExclusionCode exclusionCode, IEnumerable<string> photoDamageTypes)
    {
        ArgumentNullException.ThrowIfNull(photoDamageTypes);

        var evidence = EvidenceTypesFor(exclusionCode);
        return evidence.Count > 0 && photoDamageTypes.Any(type => type is not null && evidence.Contains(type));
    }

    private static FrozenSet<string> Set(params string[] damageTypes) => damageTypes.ToFrozenSet(StringComparer.Ordinal);
}
