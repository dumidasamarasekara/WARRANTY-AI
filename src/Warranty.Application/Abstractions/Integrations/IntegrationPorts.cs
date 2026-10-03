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
}

public sealed record ServiceCenterInfo(Guid Id, string Name, Region Region);

/// <summary>Creates simulated repair requests; called only by the ActionExecutor (constitution II).</summary>
public interface IRepairRequestService
{
    Task<Guid> CreateAsync(Guid claimId, Guid serviceCenterId, CancellationToken ct);
}

/// <summary>Writes simulated customer notifications to an outbox; nothing is sent in the PoC.</summary>
public interface INotificationService
{
    Task<Guid> EnqueueAsync(Guid claimId, string template, CancellationToken ct);
}
