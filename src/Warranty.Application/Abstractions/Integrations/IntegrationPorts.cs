using Warranty.Domain.Common;
using Warranty.Domain.Crm;

namespace Warranty.Application.Abstractions.Integrations;

/// <summary>Customer master data (simulated CRM). Customers are matched by (tenant, normalized email).</summary>
public interface ICrmClient
{
    Task<Customer> FindOrCreateCustomerAsync(CustomerDetails details, CancellationToken ct);

    Task<Customer?> GetCustomerAsync(Guid customerId, CancellationToken ct);
}

public sealed record CustomerDetails(
    string FullName,
    string Email,
    string Country,
    string? Phone,
    string? AddressLine,
    string? City,
    string? PostalCode);

/// <summary>Serial registry of the current tenant's catalog (simulated ERP).</summary>
public interface IErpSerialRegistry
{
    Task<SerialLookup> LookupAsync(string modelCode, string serialNumber, CancellationToken ct);
}

/// <summary>Whether the model and the serial are registered, and to which product.</summary>
public sealed record SerialLookup(bool ModelFound, bool SerialRegistered, Guid? ProductId, string? ProductName, string? Category);

/// <summary>Service centers of the current tenant (simulated service network).</summary>
public interface IServiceNetwork
{
    Task<ServiceCenterInfo?> FindServiceCenterAsync(Region region, string productCategory, CancellationToken ct);

    /// <summary>
    /// The active centres of the region, ordered by priority (lower first) then name; each says whether it
    /// repairs the product category (<see cref="EligibleServiceCenter.ServesClaim"/>). Inactive centres are never listed.
    /// </summary>
    Task<IReadOnlyList<EligibleServiceCenter>> ListEligibleAsync(Region region, string productCategory, CancellationToken ct);
}

public sealed record ServiceCenterInfo(Guid Id, string Name, Region Region);

/// <summary>
/// A selectable repair centre. <see cref="Contact"/> is staff-only and must not reach claimant projections;
/// <see cref="ServesClaim"/> is true when the centre lists the claim's product category among its capabilities.
/// </summary>
public sealed record EligibleServiceCenter(
    Guid Id,
    string Name,
    Region Region,
    string? Address,
    string? OpeningHours,
    string? Contact,
    int Priority,
    bool ServesClaim);

/// <summary>Creates simulated repair requests; called only by the ActionExecutor (constitution II).</summary>
public interface IRepairRequestService
{
    Task<Guid> CreateAsync(Guid claimId, Guid serviceCenterId, CancellationToken ct);
}

/// <summary>Writes simulated customer notifications to an outbox; nothing is sent in the PoC.</summary>
public interface INotificationService
{
    Task<Guid> EnqueueAsync(Guid claimId, string template, CancellationToken ct);

    /// <summary>
    /// Queues a notification at most once per tenant for <paramref name="dedupeKey"/> (e.g. <c>repair-ready:{attemptId}</c>,
    /// research R7). A key that is already queued is reported as <see cref="NotificationEnqueueResult.AlreadyQueued"/>, not as an error.
    /// </summary>
    Task<NotificationEnqueueResult> EnqueueAsync(Guid claimId, string template, string dedupeKey, CancellationToken ct);
}

/// <summary>Outcome of a de-duplicated enqueue: the new notification's ID, or <see cref="AlreadyQueued"/> with no ID.</summary>
public sealed record NotificationEnqueueResult(Guid? NotificationId, bool AlreadyQueued)
{
    public static NotificationEnqueueResult Queued(Guid notificationId) => new(notificationId, false);

    public static NotificationEnqueueResult Duplicate { get; } = new(null, true);
}

/// <summary>
/// Hands a refund to the application's refund option (simulated in the PoC; no money moves, research R10).
/// Called only by the ActionExecutor when a claim's remedy becomes Refund (constitution II).
/// </summary>
public interface IRefundService
{
    /// <summary>Requests a refund of <paramref name="amount"/> for the claim; returns the refund request ID (<c>refund_request_id</c>).</summary>
    Task<Guid> RequestRefundAsync(Guid claimId, decimal amount, CancellationToken ct);
}
