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

    /// <summary>Postal address shown to claimants; required for active centres once seeded.</summary>
    public string? Address { get; private set; }

    /// <summary>Opening hours shown to claimants, e.g. <c>Mon–Fri 09:00–17:00</c>.</summary>
    public string? OpeningHours { get; private set; }

    /// <summary>Staff-only phone/email of the centre; never shown to claimants.</summary>
    public string? Contact { get; private set; }

    /// <summary>Inactive centres are never suggested or selectable.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>Lower value is suggested first; ties are broken by name.</summary>
    public int Priority { get; private set; } = DefaultPriority;

    public const int DefaultPriority = 100;

    /// <summary>Only these centres are replacement locations (003 FR-007); false unless seeded otherwise.</summary>
    public bool IssuesReplacements { get; private set; }

    public static ServiceCenter Create(
        Guid id,
        Guid tenantId,
        Region region,
        string name,
        IEnumerable<string> capabilities,
        string? address = null,
        string? openingHours = null,
        string? contact = null,
        bool isActive = true,
        int priority = DefaultPriority,
        bool issuesReplacements = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new ServiceCenter
        {
            Id = id,
            TenantId = tenantId,
            Region = region,
            Name = name.Trim(),
            Capabilities = capabilities.Select(c => c.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray(),
            Address = NullIfBlank(address),
            OpeningHours = NullIfBlank(openingHours),
            Contact = NullIfBlank(contact),
            IsActive = isActive,
            Priority = priority,
            IssuesReplacements = issuesReplacements,
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

    /// <summary>
    /// Optional idempotency key, unique per tenant when set (e.g. <c>repair-ready:{attemptId}</c>); null for
    /// notifications that need no de-duplication.
    /// </summary>
    public string? DedupeKey { get; private set; }

    public static Notification Create(
        Guid id,
        Guid tenantId,
        Guid claimId,
        string template,
        DateTimeOffset createdAt,
        string? dedupeKey = null)
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
            DedupeKey = string.IsNullOrWhiteSpace(dedupeKey) ? null : dedupeKey.Trim(),
        };
    }
}
