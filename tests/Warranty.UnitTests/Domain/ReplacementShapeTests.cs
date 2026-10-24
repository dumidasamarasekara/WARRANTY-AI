using Warranty.Domain.Fulfillment;

namespace Warranty.UnitTests.Domain;

public sealed class ReplacementShapeTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid Claim = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid Location = Guid.Parse("33333333-3333-7333-8333-333333333333");
    private static readonly Guid PolicyVersion = Guid.Parse("44444444-4444-7444-8444-444444444444");
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private static Replacement Create(
        ReplacementStatus status = ReplacementStatus.AwaitingUnit,
        ReplacementReturnRule returnRule = ReplacementReturnRule.AtCollection,
        Guid? location = null,
        int minimumCoverMonths = 0,
        Guid? id = null,
        Guid? tenant = null,
        Guid? claim = null,
        Guid? policyVersion = null) =>
        Replacement.Create(
            id ?? Guid.CreateVersion7(),
            tenant ?? Tenant,
            claim ?? Claim,
            policyVersion ?? PolicyVersion,
            ReplacementProductRule.SameCategoryAtLeastClaimValue,
            returnRule,
            minimumCoverMonths,
            status,
            location,
            Now);

    [Fact]
    public void Create_with_a_suggested_location_assigns_it_and_records_LocationSuggested()
    {
        var replacement = Create(location: Location, minimumCoverMonths: 6);

        Assert.Equal(ReplacementStatus.AwaitingUnit, replacement.Status);
        Assert.Equal(ReturnState.NotYetReturned, replacement.ReturnState);
        Assert.Equal(PolicyVersion, replacement.PolicyVersionId);
        Assert.Equal(ReplacementProductRule.SameCategoryAtLeastClaimValue, replacement.ProductRule);
        Assert.Equal(ReplacementReturnRule.AtCollection, replacement.ReturnRule);
        Assert.Equal(6, replacement.MinimumCoverMonths);
        Assert.Null(replacement.CurrentUnit);
        Assert.Null(replacement.PendingProposal);
        Assert.Null(replacement.ApprovedProductId);
        Assert.Null(replacement.Return);
        Assert.Empty(replacement.Units);
        Assert.Empty(replacement.Proposals);

        var location = Assert.Single(replacement.Locations);
        Assert.Same(location, replacement.ActiveLocation);
        Assert.Equal(Location, location.ServiceCenterId);
        Assert.Equal(LocationAssignment.Suggested, location.Assignment);
        Assert.Equal(ReplacementLocation.SystemRecorder, location.RecordedBy);
        Assert.True(location.IsActive);
        Assert.Null(location.Reason);
        Assert.Equal(replacement.Id, location.ReplacementId);
        Assert.Equal(Tenant, location.TenantId);

        var replacementEvent = Assert.Single(replacement.Events);
        Assert.Equal(1, replacementEvent.Seq);
        Assert.Equal(ReplacementEventType.LocationSuggested, replacementEvent.Type);
        Assert.Null(replacementEvent.FromStatus);
        Assert.Equal(ReplacementStatus.AwaitingUnit, replacementEvent.ToStatus);
        Assert.Equal(ReportSource.System, replacementEvent.Source);
        Assert.Equal(ReplacementEvent.SystemActor, replacementEvent.Actor);
        Assert.Equal(Claim, replacementEvent.ClaimId);
        Assert.Equal(replacement.Id, replacementEvent.ReplacementId);
        Assert.Contains(Location.ToString(), replacementEvent.PayloadJson, StringComparison.Ordinal);
        Assert.Null(replacementEvent.CorrectsEventId);
    }

    [Fact]
    public void Create_without_a_location_has_no_location_and_no_event()
    {
        var replacement = Create();

        Assert.Equal(ReplacementStatus.AwaitingUnit, replacement.Status);
        Assert.Null(replacement.ActiveLocationId);
        Assert.Empty(replacement.Locations);
        Assert.Empty(replacement.Events);
    }

    [Fact]
    public void Create_awaits_the_return_first_only_under_BeforeReservation()
    {
        var replacement = Create(ReplacementStatus.AwaitingReturn, ReplacementReturnRule.BeforeReservation, Location);

        Assert.Equal(ReplacementStatus.AwaitingReturn, replacement.Status);
        Assert.Equal(ReplacementStatus.AwaitingReturn, Assert.Single(replacement.Events).ToStatus);

        Assert.Throws<ArgumentException>(() => Create(ReplacementStatus.AwaitingReturn, ReplacementReturnRule.AtCollection));
    }

    [Theory]
    [InlineData(ReplacementStatus.ReadyForCollection)]
    [InlineData(ReplacementStatus.HandedToCustomer)]
    [InlineData(ReplacementStatus.NotAvailable)]
    [InlineData(ReplacementStatus.Cancelled)]
    public void Create_rejects_any_other_initial_status(ReplacementStatus status) =>
        Assert.Throws<ArgumentException>(() => Create(status));

    [Theory]
    [InlineData(-1)]
    [InlineData(37)]
    public void Create_rejects_a_minimum_cover_outside_0_to_36_months(int months) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(minimumCoverMonths: months));

    [Fact]
    public void Create_rejects_empty_ids()
    {
        Assert.Throws<ArgumentException>(() => Create(id: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Create(tenant: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Create(claim: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Create(policyVersion: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Create(location: Guid.Empty));
    }
}
