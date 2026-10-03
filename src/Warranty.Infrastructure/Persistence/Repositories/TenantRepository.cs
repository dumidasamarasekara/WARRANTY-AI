using Microsoft.EntityFrameworkCore;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Tenancy;

namespace Warranty.Infrastructure.Persistence.Repositories;

/// <summary>
/// Platform lookups for tenant resolution. They run before a tenant context exists, so they open a
/// <see cref="NoTenantScope"/>: <c>tenancy.tenants</c> and <c>tenancy.tenant_channels</c> have no
/// row-level security, while every tenant-owned table stays empty on that connection.
/// </summary>
internal sealed class TenantRepository(WarrantyDbContext db) : ITenantRepository
{
    public async Task<Tenant?> GetActiveTenantAsync(Guid tenantId, CancellationToken ct)
    {
        using var _ = NoTenantScope.Begin();
        return await db.Tenants.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, ct);
    }

    public async Task<Tenant?> FindByChannelHostAsync(string hostname, CancellationToken ct)
    {
        var host = TenantChannel.NormalizeHost(hostname);
        using var _ = NoTenantScope.Begin();
        return await db.TenantChannels.AsNoTracking()
            .Where(c => c.Hostname == host)
            .Join(db.Tenants, c => c.TenantId, t => t.Id, (_, t) => t)
            .Where(t => t.Status == TenantStatus.Active)
            .SingleOrDefaultAsync(ct);
    }

    public async Task<TenantSettings> GetCurrentSettingsAsync(CancellationToken ct)
        => await db.TenantSettings.SingleOrDefaultAsync(ct)
           ?? throw new InvalidOperationException("The current tenant has no settings.");
}
