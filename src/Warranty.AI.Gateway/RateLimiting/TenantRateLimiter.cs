using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Warranty.AI.Gateway.Routing;

namespace Warranty.AI.Gateway.RateLimiting;

/// <summary>
/// One token bucket per tenant (research R21), so one tenant cannot exhaust the shared provider quota:
/// a burst of up to <c>RateLimits:PerTenantRequestsPerMinute</c> calls, refilled evenly over a minute.
/// A caller waits for a token up to its own timeout.
/// </summary>
public sealed class TenantRateLimiter : IDisposable
{
    private readonly ConcurrentDictionary<Guid, TokenBucketRateLimiter> _buckets = new();
    private readonly int _perMinute;

    public TenantRateLimiter(IOptions<AiGatewayOptions> options)
    {
        _perMinute = options.Value.RateLimits.PerTenantRequestsPerMinute;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_perMinute);
    }

    /// <summary>True when a call may start within <paramref name="wait"/>; false when the tenant's budget stays exhausted.</summary>
    public async Task<bool> TryAcquireAsync(Guid tenantId, TimeSpan wait, CancellationToken ct)
    {
        var bucket = _buckets.GetOrAdd(tenantId, _ => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = _perMinute,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1) / _perMinute,
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        }));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        try
        {
            using var lease = await bucket.AcquireAsync(1, timeout.Token);
            return lease.IsAcquired;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var bucket in _buckets.Values)
        {
            bucket.Dispose();
        }
    }
}
