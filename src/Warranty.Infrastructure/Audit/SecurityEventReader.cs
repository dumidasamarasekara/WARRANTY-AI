using Microsoft.EntityFrameworkCore;
using Warranty.Application.Abstractions.Audit;
using Warranty.Domain.Common;
using Warranty.Infrastructure.Persistence;

namespace Warranty.Infrastructure.Audit;

/// <summary>
/// Reads the current tenant's security events from <c>audit.security_events</c> (FR-041a). The connection
/// runs as <c>warranty_app</c> under the tenant session, so row-level security returns only this tenant's
/// rows and hides operator-only kinds (<c>tenant_id = NULL</c>); those kinds are excluded again here.
/// Details and source IP are never read. Newest first, by occurrence then id (a time-ordered UUID v7).
/// </summary>
internal sealed class SecurityEventReader(WarrantyDbContext db) : ISecurityEventReader
{
    public async Task<SecurityEventPageResult> ListAsync(SecurityEventKind? kind, int page, int pageSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var query = db.SecurityEvents.AsNoTracking()
            .Where(e => e.Kind != SecurityEventKind.CrossTenantAccessDenied && e.Kind != SecurityEventKind.UnknownChannel);
        if (kind is { } only)
        {
            query = query.Where(e => e.Kind == only);
        }

        var total = await query.CountAsync(ct);
        var events = await query
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return new SecurityEventPageResult(events, total);
    }
}
