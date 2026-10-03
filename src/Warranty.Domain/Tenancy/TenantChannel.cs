namespace Warranty.Domain.Tenancy;

/// <summary>Maps a claimant-channel hostname (e.g. <c>aurora.localhost</c>) to its tenant (research R9).</summary>
public sealed class TenantChannel
{
    private TenantChannel()
    {
        Hostname = string.Empty;
    }

    /// <summary>Lower-case host name without port; matched exactly.</summary>
    public string Hostname { get; private set; }

    public Guid TenantId { get; private set; }

    public static TenantChannel Create(string hostname, Guid tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        }

        return new TenantChannel { Hostname = NormalizeHost(hostname), TenantId = tenantId };
    }

    /// <summary>Lower-cases a Host header value and strips any port, as used for channel lookup.</summary>
    public static string NormalizeHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var value = host.Trim().ToLowerInvariant();
        var colon = value.LastIndexOf(':');
        return colon > 0 && !value.Contains(']', StringComparison.Ordinal) ? value[..colon] : value;
    }
}
