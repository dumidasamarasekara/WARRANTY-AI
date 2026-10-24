using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public async Task Service_network_lists_active_centres_by_priority_then_name_and_flags_those_serving_the_category()
    {
        var integration = Substitute.For<IIntegrationRepository>();
        var zeta = ServiceCenter.Create(Guid.NewGuid(), Aurora, Region.NA, "Zeta", ["tablet"], priority: 10);
        var beta = ServiceCenter.Create(Guid.NewGuid(), Aurora, Region.NA, "Beta", ["phone"], priority: 20);
        var alpha = ServiceCenter.Create(Guid.NewGuid(), Aurora, Region.NA, "Alpha", ["Tablet"], priority: 20);
        var closed = ServiceCenter.Create(Guid.NewGuid(), Aurora, Region.NA, "Closed", ["tablet"], isActive: false, priority: 1);
        integration.GetServiceCentersAsync(Region.NA, Ct).Returns([beta, closed, zeta, alpha]);

        var eligible = await new SimulatedServiceNetwork(integration).ListEligibleAsync(Region.NA, " Tablet ", ServiceCenterPurpose.Repair, Ct);

        eligible.Select(c => c.Id).ShouldBe([zeta.Id, alpha.Id, beta.Id]);
        eligible.Select(c => c.ServesClaim).ShouldBe([true, true, false]);
    }

    [Fact]
    public async Task Service_network_suggests_only_active_centres_and_prefers_the_higher_priority()
    {
        var integration = Substitute.For<IIntegrationRepository>();
        var closed = ServiceCenter.Create(Guid.NewGuid(), Aurora, Region.NA, "A Closed", ["tablet"], isActive: false, priority: 1);
        var backup = ServiceCenter.Create(Guid.NewGuid(), Aurora, Region.NA, "B Backup", ["tablet"], priority: 50);
        var preferred = ServiceCenter.Create(Guid.NewGuid(), Aurora, Region.NA, "C Preferred", ["tablet"], priority: 10);
        var general = ServiceCenter.Create(Guid.NewGuid(), Aurora, Region.NA, "D General", ["phone"], priority: 5);
        var closedOnly = ServiceCenter.Create(Guid.NewGuid(), Aurora, Region.EU, "E Closed", ["tablet"], isActive: false);
        integration.GetServiceCentersAsync(Region.NA, Ct).Returns([closed, backup, preferred, general]);
        integration.GetServiceCentersAsync(Region.EU, Ct).Returns([closedOnly]);
        var network = new SimulatedServiceNetwork(integration);

        (await network.FindServiceCenterAsync(Region.NA, "tablet", Ct))!.Id.ShouldBe(preferred.Id);
        (await network.FindServiceCenterAsync(Region.NA, "laptop", Ct))!.Id.ShouldBe(general.Id);
        (await network.FindServiceCenterAsync(Region.EU, "tablet", Ct)).ShouldBeNull();
        (await network.ListEligibleAsync(Region.EU, "tablet", ServiceCenterPurpose.Repair, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refund_request_is_recorded_with_a_new_id_and_moves_no_money()
    {
        var integration = Substitute.For<IIntegrationRepository>();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));
        var logger = new RecordingLogger<SimulatedRefundService>();
        var refunds = new SimulatedRefundService(new FakeTenantContext(Aurora), time, logger);
        var claimId = Guid.NewGuid();

        var first = await refunds.RequestRefundAsync(claimId, 449.99m, Ct);
        var second = await refunds.RequestRefundAsync(claimId, 449.99m, Ct);

        first.ShouldNotBe(Guid.Empty);
        second.ShouldNotBe(first);
        logger.Entries.Count.ShouldBe(2);
        var entry = logger.Entries[0];
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldContain(first.ToString());
        entry.Message.ShouldContain(claimId.ToString());
        entry.Message.ShouldContain(Aurora.ToString());
        entry.Message.ShouldContain("no money moved");
        integration.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refund_needs_a_claim_and_a_positive_amount()
    {
        var refunds = new SimulatedRefundService(
            new FakeTenantContext(Aurora), TimeProvider.System, new RecordingLogger<SimulatedRefundService>());

        await Should.ThrowAsync<ArgumentException>(() => refunds.RequestRefundAsync(Guid.Empty, 10m, Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => refunds.RequestRefundAsync(Guid.NewGuid(), 0m, Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => refunds.RequestRefundAsync(Guid.NewGuid(), -1m, Ct));
    }

    [Fact]
    public async Task A_deduplicated_notification_carries_its_key()
    {
        var integration = Substitute.For<IIntegrationRepository>();
        integration.TryAddNotificationAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>()).Returns(true);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));
        var claimId = Guid.NewGuid();

        var result = await new SimulatedNotificationService(integration, new FakeTenantContext(Aurora), time)
            .EnqueueAsync(claimId, "RepairReadyForCollection", "repair-ready:42", Ct);

        result.AlreadyQueued.ShouldBeFalse();
        await integration.Received(1).TryAddNotificationAsync(
            Arg.Is<Notification>(n =>
                n.Id == result.NotificationId && n.TenantId == Aurora && n.ClaimId == claimId && n.DedupeKey == "repair-ready:42"),
            Arg.Any<CancellationToken>());
        integration.DidNotReceive().AddNotification(Arg.Any<Notification>());
    }

    [Fact]
    public async Task A_dedupe_key_that_is_already_queued_is_reported_as_duplicate()
    {
        var integration = Substitute.For<IIntegrationRepository>();
        integration.TryAddNotificationAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>()).Returns(false);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));

        var result = await new SimulatedNotificationService(integration, new FakeTenantContext(Aurora), time)
            .EnqueueAsync(Guid.NewGuid(), "RepairReadyForCollection", "repair-ready:42", Ct);

        result.ShouldBe(NotificationEnqueueResult.Duplicate);
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

        foreach (var port in new[] { typeof(ICrmClient), typeof(IErpSerialRegistry), typeof(IServiceNetwork), typeof(IRepairRequestService), typeof(INotificationService), typeof(IRefundService) })
        {
            services.ShouldContain(d => d.ServiceType == port && d.Lifetime == ServiceLifetime.Scoped, port.Name);
        }
    }

    private static CustomerDetails Details(string email) => new("Jane Example", email, "US", null, null, null, null);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
