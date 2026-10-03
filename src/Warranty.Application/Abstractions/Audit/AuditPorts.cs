using Warranty.Domain.Common;

namespace Warranty.Application.Abstractions.Audit;

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
}
