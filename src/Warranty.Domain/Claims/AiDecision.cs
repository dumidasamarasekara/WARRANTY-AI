using System.Text.Json.Serialization;

namespace Warranty.Domain.Claims;

/// <summary>The decision an AI recommendation proposes (FR-020); advisory only, never executed directly.</summary>
public enum AiDecision
{
    [JsonStringEnumMemberName("APPROVE")]
    Approve,

    [JsonStringEnumMemberName("REJECT")]
    Reject,

    [JsonStringEnumMemberName("REQUEST_MORE_INFORMATION")]
    RequestMoreInformation,

    [JsonStringEnumMemberName("HUMAN_REVIEW")]
    HumanReview,
}
