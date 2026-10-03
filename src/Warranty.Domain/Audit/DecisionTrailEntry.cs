using Warranty.Domain.Common;

namespace Warranty.Domain.Audit;

/// <summary>
/// One immutable, hash-chained step of a claim's decision trail (FR-038, FR-039). The writer assigns
/// the per-claim gap-free sequence and computes <c>hash = SHA-256(prev_hash ‖ canonical_json)</c>.
/// </summary>
public sealed class DecisionTrailEntry
{
    private DecisionTrailEntry()
    {
        Actor = Summary = PayloadJson = CorrelationId = PrevHash = Hash = string.Empty;
    }

    public long Id { get; private init; }

    public Guid TenantId { get; private init; }

    public Guid ClaimId { get; private init; }

    public int Seq { get; private init; }

    public DateTimeOffset OccurredAt { get; private init; }

    public TrailStep Step { get; private init; }

    /// <summary><c>system</c>, an agent name, or a staff <c>sub</c>.</summary>
    public string Actor { get; private init; }

    /// <summary>One-line human-readable summary rendered in the trace UI.</summary>
    public string Summary { get; private init; }

    public string PayloadJson { get; private init; }

    public string CorrelationId { get; private init; }

    public string PrevHash { get; private init; }

    public string Hash { get; private init; }

    public static DecisionTrailEntry Create(
        Guid tenantId, Guid claimId, int seq, DateTimeOffset occurredAt, TrailStep step, string actor, string summary,
        string payloadJson, string correlationId, string prevHash, string hash)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seq);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        return new DecisionTrailEntry
        {
            TenantId = tenantId,
            ClaimId = claimId,
            Seq = seq,
            OccurredAt = occurredAt,
            Step = step,
            Actor = actor,
            Summary = summary,
            PayloadJson = payloadJson ?? "{}",
            CorrelationId = correlationId ?? string.Empty,
            PrevHash = prevHash ?? string.Empty,
            Hash = hash,
        };
    }
}
