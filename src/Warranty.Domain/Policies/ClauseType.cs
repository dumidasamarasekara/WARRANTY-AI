using System.Text.Json.Serialization;

namespace Warranty.Domain.Policies;

/// <summary>Kind of a policy clause, declared in the source front matter (contracts/rag.md).</summary>
public enum ClauseType
{
    Coverage,
    Period,
    Exclusion,
    ServiceRule,
    Definition,
}

/// <summary>Structured exclusion a policy version can list in <c>terms.exclusions</c> (research R26).</summary>
public enum ExclusionCode
{
    [JsonStringEnumMemberName("ACCIDENTAL_DAMAGE")]
    AccidentalDamage,

    [JsonStringEnumMemberName("LIQUID_DAMAGE")]
    LiquidDamage,

    [JsonStringEnumMemberName("COSMETIC_DAMAGE")]
    CosmeticDamage,

    [JsonStringEnumMemberName("UNAUTHORIZED_REPAIR")]
    UnauthorizedRepair,
}
