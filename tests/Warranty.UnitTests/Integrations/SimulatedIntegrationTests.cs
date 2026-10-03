using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Catalog;
using Warranty.Domain.Common;
using Warranty.Domain.Crm;
using Warranty.Domain.Integration;
using Warranty.Integrations.Simulated;
using Warranty.UnitTests.Infrastructure;

namespace Warranty.UnitTests.Integrations;

public sealed class SimulatedIntegrationTests
{
    private static readonly Guid Aurora = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Crm_returns_the_existing_customer_for_a_normalized_email_without_adding()
    {
        var customers = Substitute.For<ICustomerRepository>();
        var existing = Customer.Create(Guid.NewGuid(), Aurora, "Jane Example", "jane@example.test", "US");
        customers.FindByEmailAsync("jane@example.test", Ct).Returns(existing);

        var customer = await new SimulatedCrmClient(customers, new FakeTenantContext(Aurora))
            .FindOrCreateCustomerAsync(Details("  Jane@Example.TEST "), Ct);

        customer.ShouldBeSameAs(existing);
        customers.DidNotReceive().Add(Arg.Any<Customer>());
    }

    [Fact]
    public async Task Crm_creates_an_unknown_customer_in_the_current_tenant()
    {
        var customers = Substitute.For<ICustomerRepository>();

        var customer = await new SimulatedCrmClient(customers, new FakeTenantContext(Aurora))
            .FindOrCreateCustomerAsync(Details("New@Example.test"), Ct);

        customer.TenantId.ShouldBe(Aurora);
        customer.Email.ShouldBe("new@example.test");
        customers.Received(1).Add(customer);
    }

    [Fact]
    public async Task Erp_registers_a_serial_only_for_its_own_model()
    {
        var catalog = Substitute.For<ICatalogRepository>();
        var tablet = Product.Create(Guid.NewGuid(), Aurora, "AUR-TAB10", "Aurora Tab 10", "tablet", 450m, "USD");
        var phone = Product.Create(Guid.NewGuid(), Aurora, "AUR-PHN6", "Aurora Phone 6", "phone", 480m, "USD");
        catalog.FindProductByModelAsync("AUR-TAB10", Ct).Returns(tablet);
        catalog.FindSerialAsync("TAB-1", Ct).Returns(ProductSerial.Create(Aurora, "TAB-1", tablet.Id));
        catalog.FindSerialAsync("PHN-1", Ct).Returns(ProductSerial.Create(Aurora, "PHN-1", phone.Id));
        var erp = new SimulatedErpSerialRegistry(catalog);

        (await erp.LookupAsync("AUR-TAB10", "TAB-1", Ct)).ShouldBe(new SerialLookup(true, true, tablet.Id, "Aurora Tab 10", "tablet"));
        (await erp.LookupAsync("AUR-TAB10", "PHN-1", Ct)).SerialRegistered.ShouldBeFalse();
        (await erp.LookupAsync("AUR-TAB10", "UNKNOWN", Ct)).SerialRegistered.ShouldBeFalse();
        (await erp.LookupAsync("NOPE", "TAB-1", Ct)).ShouldBe(new SerialLookup(false, false, null, null, null));
    }

    [Fact]
    public async Task Service_network_prefers_a_center_with_the_category_and_falls_back_to_the_region()
    {
        var integration = Substitute.For<IIntegrationRepository>();
        var general = ServiceCenter.Create(Guid.NewGuid(), Aurora, Region.NA, "A General", ["phone"]);
        var tablets = ServiceCenter.Create(Guid.NewGuid(), Aurora, Region.NA, "B Tablets", ["Tablet"]);
        integration.GetServiceCentersAsync(Region.NA, Ct).Returns([general, tablets]);
        integration.GetServiceCentersAsync(Region.EU, Ct).Returns([]);
        var network = new SimulatedServiceNetwork(integration);

        (await network.FindServiceCenterAsync(Region.NA, "tablet", Ct))!.Id.ShouldBe(tablets.Id);
        (await network.FindServiceCenterAsync(Region.NA, "laptop", Ct))!.Id.ShouldBe(general.Id);
        (await network.FindServiceCenterAsync(Region.EU, "tablet", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Repair_requests_and_notifications_are_added_to_the_unit_of_work_for_the_current_tenant()
    {
        var integration = Substitute.For<IIntegrationRepository>();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));
        var tenant = new FakeTenantContext(Aurora);
        var claimId = Guid.NewGuid();
        var centerId = Guid.NewGuid();

        var repairId = await new SimulatedRepairRequestService(integration, tenant, time).CreateAsync(claimId, centerId, Ct);
        var notificationId = await new SimulatedNotificationService(integration, tenant, time).EnqueueAsync(claimId, "claim-approved", Ct);

        integration.Received(1).AddRepairRequest(Arg.Is<RepairRequest>(r =>
            r.Id == repairId && r.TenantId == Aurora && r.ClaimId == claimId && r.ServiceCenterId == centerId && r.CreatedAt == time.GetUtcNow()));
        integration.Received(1).AddNotification(Arg.Is<Notification>(n =>
            n.Id == notificationId && n.TenantId == Aurora && n.Template == "claim-approved" && n.Channel == NotificationChannel.Email));
    }

    [Fact]
    public void Every_integration_port_is_registered()
    {
        var services = new ServiceCollection().AddSimulatedIntegrations();

        foreach (var port in new[] { typeof(ICrmClient), typeof(IErpSerialRegistry), typeof(IServiceNetwork), typeof(IRepairRequestService), typeof(INotificationService) })
        {
            services.ShouldContain(d => d.ServiceType == port && d.Lifetime == ServiceLifetime.Scoped, port.Name);
        }
    }

    private static CustomerDetails Details(string email) => new("Jane Example", email, "US", null, null, null, null);
}
