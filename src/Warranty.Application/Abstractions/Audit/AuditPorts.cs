using Warranty.Domain.Audit;
using Warranty.Domain.Common;

namespace Warranty.Application.Abstractions.Audit;

/// <summary>A claim's decision trail as stored, ordered by <c>seq</c>, with the result of re-verifying its hash chain.</summary>
public sealed record DecisionTrailSnapshot(IReadOnlyList<DecisionTrailEntry> Entries, bool HashChainValid);

/// <summary>Reads the decision trail of a claim of the current tenant (FR-041) and re-verifies its hash chain (FR-039).</summary>
public interface IDecisionTrailReader
{
    Task<DecisionTrailSnapshot> ReadAsync(Guid claimId, CancellationToken ct);
}

/// <summary>
/// Appends hash-chained entries to a claim's decision trail of the current tenant (FR-038, FR-039).
/// Entries are never modified; corrections are appended as <see cref="TrailStep.Correction"/>.
/// </summary>
public interface IDecisionTrailWriter
{
    Task AppendAsync(Guid claimId, TrailStep step, string actor, string summary, object? payload, CancellationToken ct);
}

/// <summary>
/// Records security events (FR-005, FR-037a, FR-041a). Tenant events belong to the current tenant;
/// operator-only kinds are written without a tenant through a dedicated database function (research R30).
/// </summary>
public interface ISecurityEventWriter
{
    /// <summary>Records an event for the current tenant (e.g. <see cref="SecurityEventKind.AccessDenied"/>).</summary>
    Task RecordAsync(SecurityEventKind kind, string actor, string? target, object? details, CancellationToken ct);

    /// <summary>Records an operator-only event (e.g. <see cref="SecurityEventKind.UnknownChannel"/>); no tenant is attached.</summary>
    Task RecordOperatorEventAsync(SecurityEventKind kind, string actor, string? target, object? details, CancellationToken ct);

    /// <summary>
    /// Records the operator-only <see cref="SecurityEventKind.CrossTenantAccessDenied"/> when <paramref name="id"/>
    /// exists in a tenant other than the current one, with both tenants in its details; returns whether it did.
    /// The other tenant is never returned to the caller (research R30).
    /// </summary>
    Task<bool> RecordCrossTenantDenialAsync(ScopedIdKind kind, Guid id, string actor, string target, CancellationToken ct);
}

/// <summary>The kind of tenant-scoped ID a staff request names.</summary>
public enum ScopedIdKind
{
    Claim,
    Evidence,
}
