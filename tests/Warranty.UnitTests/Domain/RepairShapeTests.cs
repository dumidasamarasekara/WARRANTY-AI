using Warranty.Domain.Fulfillment;

namespace Warranty.UnitTests.Domain;

public sealed class RepairShapeTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid Claim = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid Centre = Guid.Parse("33333333-3333-7333-8333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_with_a_suggested_centre_assigns_it_and_records_CentreSuggested()
    {
        var repair = Repair.Create(Guid.CreateVersion7(), Tenant, Claim, Centre, Now);

        Assert.Equal(RepairStatus.AwaitingHandover, repair.Status);
        Assert.Null(repair.NotPossibleReason);
        Assert.Null(repair.CurrentAttempt);
        Assert.Equal(0, repair.FailedAttempts);

        var handover = Assert.Single(repair.Handovers);
        Assert.Same(handover, repair.ActiveHandover);
        Assert.Equal(Centre, handover.ServiceCenterId);
        Assert.Equal(HandoverAssignment.Suggested, handover.Assignment);
        Assert.Equal(RepairHandover.SystemRecorder, handover.RecordedBy);
        Assert.True(handover.IsActive);
        Assert.Null(handover.HandedOverOn);
        Assert.Equal(repair.Id, handover.RepairId);
        Assert.Equal(Tenant, handover.TenantId);

        var repairEvent = Assert.Single(repair.Events);
        Assert.Equal(1, repairEvent.Seq);
        Assert.Equal(RepairEventType.CentreSuggested, repairEvent.Type);
        Assert.Null(repairEvent.FromStatus);
        Assert.Equal(RepairStatus.AwaitingHandover, repairEvent.ToStatus);
        Assert.Equal(ReportSource.System, repairEvent.Source);
        Assert.Equal(RepairEvent.SystemActor, repairEvent.Actor);
        Assert.Equal(Claim, repairEvent.ClaimId);
        Assert.Contains(Centre.ToString(), repairEvent.PayloadJson, StringComparison.Ordinal);
        Assert.Null(repairEvent.CorrectsEventId);
    }

    [Fact]
    public void Create_without_a_centre_has_no_handover_and_no_event()
    {
        var repair = Repair.Create(Guid.CreateVersion7(), Tenant, Claim, null, Now);

        Assert.Equal(RepairStatus.AwaitingHandover, repair.Status);
        Assert.Null(repair.ActiveHandoverId);
        Assert.Empty(repair.Handovers);
        Assert.Empty(repair.Events);
    }

    [Fact]
    public void Create_rejects_empty_ids()
    {
        Assert.Throws<ArgumentException>(() => Repair.Create(Guid.Empty, Tenant, Claim, Centre, Now));
        Assert.Throws<ArgumentException>(() => Repair.Create(Guid.CreateVersion7(), Guid.Empty, Claim, Centre, Now));
        Assert.Throws<ArgumentException>(() => Repair.Create(Guid.CreateVersion7(), Tenant, Guid.Empty, Centre, Now));
        Assert.Throws<ArgumentException>(() => Repair.Create(Guid.CreateVersion7(), Tenant, Claim, Guid.Empty, Now));
    }
}
