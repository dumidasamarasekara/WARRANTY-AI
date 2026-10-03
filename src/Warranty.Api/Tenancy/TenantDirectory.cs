using Microsoft.Extensions.Caching.Memory;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Tenancy;

namespace Warranty.Api.Tenancy;

/// <summary>An active tenant as needed to scope a request.</summary>
internal sealed record ResolvedTenant(Guid Id, string Slug);

/// <summary>
/// Cached lookups of active tenants by claimant-channel host and by ID (research R9). Only hits are
/// cached, so an unknown host is looked up — and recorded — on every request, and the cache stays
/// bounded by the number of configured channels and tenants. A suspended tenant stops resolving
/// once its entry expires.
/// </summary>
internal sealed class TenantDirectory(ITenantRepository tenants, IMemoryCache cache)
{
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>The active tenant behind a request Host; the host is lower-cased and its port stripped.</summary>
    public async Task<ResolvedTenant?> FindByChannelHostAsync(string host, CancellationToken ct)
    {
        var hostname = TenantChannel.NormalizeHost(host);
        var key = (nameof(FindByChannelHostAsync), hostname);
        if (cache.TryGetValue(key, out ResolvedTenant? cached))
        {
            return cached;
        }

        var tenant = await tenants.FindByChannelHostAsync(hostname, ct);
        return tenant is null ? null : cache.Set(key, new ResolvedTenant(tenant.Id, tenant.Slug), CacheDuration);
    }

    public async Task<ResolvedTenant?> FindActiveTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var key = (nameof(FindActiveTenantAsync), tenantId);
        if (cache.TryGetValue(key, out ResolvedTenant? cached))
        {
            return cached;
        }

        var tenant = await tenants.GetActiveTenantAsync(tenantId, ct);
        return tenant is null ? null : cache.Set(key, new ResolvedTenant(tenant.Id, tenant.Slug), CacheDuration);
    }
}
