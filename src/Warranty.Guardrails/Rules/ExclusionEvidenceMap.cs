using Warranty.Domain.Policies;

namespace Warranty.Guardrails.Rules;

/// <summary>
/// Fixed mapping from photo-analysis damage types to the exclusion they evidence (research R26):
/// <c>CRACKED_SCREEN</c>, <c>DENTS_OR_IMPACT</c> → <c>ACCIDENTAL_DAMAGE</c>; <c>LIQUID_INDICATORS</c>,
/// <c>CORROSION</c> → <c>LIQUID_DAMAGE</c>; <c>COSMETIC_WEAR</c> → <c>COSMETIC_DAMAGE</c>.
/// <c>UNAUTHORIZED_REPAIR</c> has no photo evidence type. Used by the <c>GROUNDED_IN_CLAUSE</c> check.
/// </summary>
public static class ExclusionEvidenceMap
{
    /// <summary>
    /// True when at least one photo damage type (photo-analysis schema names) maps to
    /// <paramref name="exclusionCode"/>; always false for <see cref="ExclusionCode.UnauthorizedRepair"/>.
    /// </summary>
    public static bool IsSupported(ExclusionCode exclusionCode, IEnumerable<string> photoDamageTypes)
        => throw new NotImplementedException("Pending T124.");
}
