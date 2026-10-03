using Microsoft.EntityFrameworkCore;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Common;
using Warranty.Domain.Crm;
using Warranty.Domain.Integration;

namespace Warranty.Infrastructure.Persistence.Repositories;

internal sealed class CustomerRepository(WarrantyDbContext db) : ICustomerRepository
{
    public async Task<Customer?> FindByEmailAsync(string normalizedEmail, CancellationToken ct)
        => db.Customers.Local.FirstOrDefault(c => c.Email == normalizedEmail)
           ?? await db.Customers.SingleOrDefaultAsync(c => c.Email == normalizedEmail, ct);

    public Task<Customer?> GetAsync(Guid customerId, CancellationToken ct)
        => db.Customers.SingleOrDefaultAsync(c => c.Id == customerId, ct);

    public void Add(Customer customer) => db.Customers.Add(customer);
}

internal sealed class IntegrationRepository(WarrantyDbContext db) : IIntegrationRepository
{
    public async Task<IReadOnlyList<ServiceCenter>> GetServiceCentersAsync(Region region, CancellationToken ct)
        => await db.ServiceCenters.AsNoTracking().Where(s => s.Region == region).OrderBy(s => s.Name).ThenBy(s => s.Id).ToListAsync(ct);

    public void AddRepairRequest(RepairRequest request) => db.RepairRequests.Add(request);

    public void AddNotification(Notification notification) => db.Notifications.Add(notification);
}
