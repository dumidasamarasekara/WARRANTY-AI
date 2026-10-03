using System.Text.Json.Serialization;

namespace Warranty.Domain.Claims;

/// <summary>Whether a risk signal came from deterministic code or from an AI agent's output.</summary>
public enum RiskSignalSource
{
    Deterministic,

    [JsonStringEnumMemberName("AI")]
    Ai,
}
