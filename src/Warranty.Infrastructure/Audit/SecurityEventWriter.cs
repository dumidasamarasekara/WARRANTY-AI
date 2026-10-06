using Microsoft.EntityFrameworkCore;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Domain.Audit;
using Warranty.Domain.Common;
using Warranty.Infrastructure.Persistence;

namespace Warranty.Infrastructure.Audit;

/// <summary>Supplies the client IP of the current request for security events; the API host provides it.</summary>
public interface IRequestSourceAccessor
{
    string? SourceIp { get; }
}

internal sealed class NoRequestSource : IRequestSourceAccessor
{
    public string? SourceIp => null;
}

/// <summary>
/// Append-only security events (FR-041a, research R30). Rows are written with SQL, never through the
/// change tracker, so recording an event does not save the caller's pending changes. Tenant events
/// are inserted under the tenant context (row-level security checks the tenant); operator-only kinds
/// go through <c>audit.record_operator_event</c>, the only way to store a row without a tenant.
/// </summary>
internal sealed class SecurityEventWriter(
    WarrantyDbContext db, ITenantContext tenantContext, TimeProvider time, IRequestSourceAccessor source) : ISecurityEventWriter
{
    private const int MaxActorLength = 200;
    private const int MaxTargetLength = 300;
    private const int MaxSourceIpLength = 45;

    public async Task RecordAsync(SecurityEventKind kind, string actor, string? target, object? details, CancellationToken ct)
    {
        // Create validates that the kind belongs to a tenant.
        var e = SecurityEvent.Create(
            Guid.CreateVersion7(), tenantContext.TenantId, TrailHash.Truncate(time.GetUtcNow()), kind, Clip(actor, MaxActorLength)!,
            Clip(target, MaxTargetLength), Clip(source.SourceIp, MaxSourceIpLength), details is null ? null : CanonicalJson.Serialize(details));

        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO audit.security_events (id, tenant_id, occurred_at, kind, actor, target, source_ip, details)
             VALUES ({e.Id}, {e.TenantId}, {e.OccurredAt}, {WireName.Of(e.Kind)}, {e.Actor}, {e.Target}, {e.SourceIp}, {e.DetailsJson}::jsonb)
             """,
            ct);
    }

    public async Task RecordOperatorEventAsync(SecurityEventKind kind, string actor, string? target, object? details, CancellationToken ct)
    {
        if (!kind.IsOperatorOnly())
        {
            throw new ArgumentException($"{kind} events belong to a tenant; use {nameof(RecordAsync)}.", nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var detailsJson = details is null ? null : CanonicalJson.Serialize(details);

        // Operator events can happen before a tenant is resolved (an unknown claimant channel).
        using var _ = NoTenantScope.Begin();
        await db.Database.ExecuteSqlAsync(
            $"""
             SELECT audit.record_operator_event({WireName.Of(kind)}, {Clip(actor, MaxActorLength)}, {Clip(target, MaxTargetLength)},
                                                {detailsJson}::jsonb, {Clip(source.SourceIp, MaxSourceIpLength)})
             """,
            ct);
    }

    public async Task<bool> RecordCrossTenantDenialAsync(ScopedIdKind kind, Guid id, string actor, string target, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var kindName = kind switch
        {
            ScopedIdKind.Claim => "claim",
            ScopedIdKind.Evidence => "evidence",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        // audit.record_cross_tenant_denial (003) looks past row-level security for the owning tenant and keeps it in the database.
        var recorded = await db.Database.SqlQuery<bool>(
            $"""
             SELECT audit.record_cross_tenant_denial({kindName}, {id}, {tenantContext.TenantId}, {Clip(actor, MaxActorLength)},
                                                     {Clip(target, MaxTargetLength)}, {Clip(source.SourceIp, MaxSourceIpLength)}) AS "Value"
             """).ToListAsync(ct);
        return recorded.Single();
    }

    private static string? Clip(string? value, int maxLength)
        => value is { Length: > 0 } && value.Length > maxLength ? value[..maxLength] : value;
}
