using Warranty.Domain.Common;

namespace Warranty.Domain.Audit;

/// <summary>
/// An append-only record of a denied or suspicious access attempt (FR-005, FR-037a, FR-041a).
/// Operator-only kinds carry no tenant; every other kind belongs to the tenant it happened in
/// (research R30). Tenants see only time, kind, actor and target — never details or IP.
/// </summary>
public sealed class SecurityEvent
{
    private SecurityEvent()
    {
        Actor = string.Empty;
    }

    public Guid Id { get; private init; }

    /// <summary>Null exactly for operator-only kinds.</summary>
    public Guid? TenantId { get; private init; }

    public DateTimeOffset OccurredAt { get; private init; }

    public SecurityEventKind Kind { get; private init; }

    public string Actor { get; private init; }

    /// <summary>The resource identifier exactly as the actor supplied it.</summary>
    public string? Target { get; private init; }

    public string? SourceIp { get; private init; }

    /// <summary>Operator-facing details; never returned to tenants.</summary>
    public string? DetailsJson { get; private init; }

    public static SecurityEvent Create(
        Guid id, Guid? tenantId, DateTimeOffset occurredAt, SecurityEventKind kind, string actor, string? target,
        string? sourceIp, string? detailsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (kind.IsOperatorOnly() && tenantId is not null)
        {
            throw new ArgumentException($"{kind} events are operator-only and must not belong to a tenant.", nameof(tenantId));
        }

        if (!kind.IsOperatorOnly() && (tenantId is null || tenantId == Guid.Empty))
        {
            throw new ArgumentException($"{kind} events belong to the tenant they happened in.", nameof(tenantId));
        }

        return new SecurityEvent
        {
            Id = id,
            TenantId = tenantId,
            OccurredAt = occurredAt,
            Kind = kind,
            Actor = actor,
            Target = target,
            SourceIp = sourceIp,
            DetailsJson = detailsJson,
        };
    }
}
