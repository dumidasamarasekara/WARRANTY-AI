namespace Warranty.Domain.AiOps;

public enum ModelCallStatus
{
    Ok,
    Invalid,
    Refused,
    Error,
    Timeout,
}

/// <summary>One model invocation through the AI Gateway (FR-040, constitution VII).</summary>
public sealed record ModelCall
{
    public required Guid Id { get; init; }

    public required Guid TenantId { get; init; }

    public Guid? ClaimId { get; init; }

    public Guid? RunId { get; init; }

    public required string Agent { get; init; }

    public required string Route { get; init; }

    public required string Provider { get; init; }

    public required string Model { get; init; }

    public string? PromptId { get; init; }

    public string? PromptVersion { get; init; }

    public int InputTokens { get; init; }

    public int OutputTokens { get; init; }

    public int CacheReadTokens { get; init; }

    public int CacheWriteTokens { get; init; }

    public long LatencyMs { get; init; }

    public decimal EstimatedCost { get; init; }

    public string? StopReason { get; init; }

    public required ModelCallStatus Status { get; init; }

    public int Attempt { get; init; } = 1;

    public string? Error { get; init; }

    public string? CorrelationId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
}

/// <summary>One tool invocation by an agent, including denied attempts (agents-and-tools contract).</summary>
public sealed record ToolCall
{
    public required Guid Id { get; init; }

    public required Guid TenantId { get; init; }

    public Guid? RunId { get; init; }

    public required string Agent { get; init; }

    public required string Tool { get; init; }

    /// <summary>Redacted arguments.</summary>
    public string ArgumentsJson { get; init; } = "{}";

    public required bool Allowed { get; init; }

    public string? DenialReason { get; init; }

    public string? ResultSummary { get; init; }

    public long LatencyMs { get; init; }

    public required string Status { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
}

/// <summary>One knowledge retrieval with its filters and results (constitution VII).</summary>
public sealed record RagQuery
{
    public required Guid Id { get; init; }

    public required Guid TenantId { get; init; }

    public Guid? RunId { get; init; }

    public required string Agent { get; init; }

    public required IReadOnlyList<string> Namespaces { get; init; }

    public string FiltersJson { get; init; } = "{}";

    /// <summary>Redacted query text.</summary>
    public string QueryText { get; init; } = string.Empty;

    public int TopK { get; init; }

    /// <summary>JSON array of <c>{chunkId, clauseKey, score}</c>.</summary>
    public string ResultsJson { get; init; } = "[]";

    public long LatencyMs { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
}
