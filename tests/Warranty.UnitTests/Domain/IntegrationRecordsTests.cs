using Warranty.Domain.Common;
using Warranty.Domain.Integration;

namespace Warranty.UnitTests.Domain;

public sealed class IntegrationRecordsTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");

    [Fact]
    public void Service_center_defaults_to_active_with_priority_100_and_no_contact_details()
    {
        var center = ServiceCenter.Create(Guid.NewGuid(), Tenant, Region.NA, "Center", ["phone"]);

        Assert.True(center.IsActive);
        Assert.Equal(100, center.Priority);
        Assert.Null(center.Address);
        Assert.Null(center.OpeningHours);
        Assert.Null(center.Contact);
    }

    [Fact]
    public void Service_center_keeps_trimmed_details_activity_and_priority()
    {
        var center = ServiceCenter.Create(
            Guid.NewGuid(), Tenant, Region.EU, "Center", ["phone"],
            address: " 1 Main St ", openingHours: "Mon–Fri 09:00–17:00", contact: "  ", isActive: false, priority: 10);

        Assert.Equal("1 Main St", center.Address);
        Assert.Equal("Mon–Fri 09:00–17:00", center.OpeningHours);
        Assert.Null(center.Contact);
        Assert.False(center.IsActive);
        Assert.Equal(10, center.Priority);
    }

    [Fact]
    public void Notification_dedupe_key_is_optional()
    {
        var now = DateTimeOffset.UnixEpoch;

        Assert.Null(Notification.Create(Guid.NewGuid(), Tenant, Guid.NewGuid(), "ClaimApproved", now).DedupeKey);
        Assert.Equal(
            "repair-ready:abc",
            Notification.Create(Guid.NewGuid(), Tenant, Guid.NewGuid(), "RepairReadyForCollection", now, "repair-ready:abc").DedupeKey);
    }
}
