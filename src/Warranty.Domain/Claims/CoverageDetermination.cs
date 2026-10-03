using System.Text.Json.Serialization;

namespace Warranty.Domain.Claims;

/// <summary>Coverage reading in an AI recommendation (FR-021).</summary>
public enum CoverageDetermination
{
    [JsonStringEnumMemberName("COVERED")]
    Covered,

    [JsonStringEnumMemberName("NOT_COVERED")]
    NotCovered,

    [JsonStringEnumMemberName("UNDETERMINED")]
    Undetermined,
}
