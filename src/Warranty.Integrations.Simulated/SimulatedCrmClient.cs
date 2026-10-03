using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Common;
using Warranty.Domain.Crm;

namespace Warranty.Integrations.Simulated;

/// <summary>
/// Simulated CRM over <c>crm.customers</c>: a customer is matched by tenant and normalized email; an
/// unknown email creates a customer in the current unit of work. Existing details are not updated.
/// </summary>
internal sealed class SimulatedCrmClient(ICustomerRepository customers, ITenantContext tenantContext) : ICrmClient
{
    public async Task<Customer> FindOrCreateCustomerAsync(CustomerDetails details, CancellationToken ct)
    {
        var email = ContactNormalizer.NormalizeEmail(details.Email);
        var existing = await customers.FindByEmailAsync(email, ct);
        if (existing is not null)
        {
            return existing;
        }

        var customer = Customer.Create(
            Guid.CreateVersion7(), tenantContext.TenantId, details.FullName, email, details.Country, details.Phone,
            details.AddressLine, details.City, details.PostalCode);
        customers.Add(customer);
        return customer;
    }

    public Task<Customer?> GetCustomerAsync(Guid customerId, CancellationToken ct) => customers.GetAsync(customerId, ct);
}
