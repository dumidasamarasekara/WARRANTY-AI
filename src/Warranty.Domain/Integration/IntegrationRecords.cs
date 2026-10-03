using Warranty.Domain.Common;

namespace Warranty.Domain.Integration;

/// <summary>A simulated service center of a tenant's service network.</summary>
public sealed class ServiceCenter
{
    private ServiceCenter()
    {
        Name = string.Empty;
        Capabilities = [];
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Region Region { get; private set; }

    public string Name { get; private set; }

    /// <summary>Product categories the center can repair.</summary>
    public IReadOnlyList<string> Capabilities { get; private set; }

    public static ServiceCenter Create(Guid id, Guid tenantId, Region region, string name, IEnumerable<string> capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new ServiceCenter
        {
            Id = id,
            TenantId = tenantId,
            Region = region,
            Name = name.Trim(),
            Capabilities = capabilities.Select(c => c.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray(),
        };
    }
}

public enum RepairRequestStatus
{
    Created,
}

/// <summary>A simulated repair request; written only by the ActionExecutor after an approval.</summary>
public sealed class RepairRequest
{
    private RepairRequest()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ClaimId { get; private set; }

    public Guid ServiceCenterId { get; private set; }

    public RepairRequestStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static RepairRequest Create(Guid id, Guid tenantId, Guid claimId, Guid serviceCenterId, DateTimeOffset createdAt)
        => new()
        {
            Id = id,
            TenantId = tenantId,
            ClaimId = claimId,
            ServiceCenterId = serviceCenterId,
            Status = RepairRequestStatus.Created,
            CreatedAt = createdAt,
        };
}

public enum NotificationChannel
{
    Email,
}

/// <summary>A simulated customer notification (outbox row); nothing is sent in the PoC.</summary>
public sealed class Notification
{
    private Notification()
    {
        Template = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ClaimId { get; private set; }

    public NotificationChannel Channel { get; private set; }

    public string Template { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Notification Create(Guid id, Guid tenantId, Guid claimId, string template, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        return new Notification
        {
            Id = id,
            TenantId = tenantId,
            ClaimId = claimId,
            Channel = NotificationChannel.Email,
            Template = template,
            CreatedAt = createdAt,
        };
    }
}
