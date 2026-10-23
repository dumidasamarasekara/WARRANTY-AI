using Warranty.Domain.Claims;
using Warranty.Domain.Fulfillment;

namespace Warranty.UnitTests.Domain;

/// <summary>The remedy state machine (data-model.md "Remedy") and its history/completion rows.</summary>
public sealed class ClaimRemedyTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid Version = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 9, 41, 0, TimeSpan.Zero);

    private static ClaimRemedy Choice(params RemedyType[] allowed) => ClaimRemedy.AwaitDecision(
        Guid.CreateVersion7(), Tenant, Guid.CreateVersion7(), Version, RemedyScenario.CoveredDefect, allowed,
        RemedyPendingReason.PolicyChoice, T0);

    [Fact]
    public void SetByPolicy_SingleAllowedRemedy_IsInProgress()
    {
        var remedy = ClaimRemedy.SetByPolicy(
            Guid.CreateVersion7(), Tenant, Guid.CreateVersion7(), Version, RemedyScenario.CoveredDefect,
            RemedyType.Repair, [RemedyType.Repair], T0);

        Assert.Equal(RemedyState.InProgress, remedy.State);
        Assert.Equal(RemedyType.Repair, remedy.Remedy);
        Assert.Null(remedy.PendingReason);
    }

    [Fact]
    public void SetByPolicy_RemedyOutsideAllowedOrChoice_IsRefused()
    {
        Assert.Throws<InvalidClaimTransitionException>(() => ClaimRemedy.SetByPolicy(
            Guid.CreateVersion7(), Tenant, Guid.CreateVersion7(), Version, RemedyScenario.CoveredDefect,
            RemedyType.Refund, [RemedyType.Repair], T0));
        Assert.Throws<InvalidClaimTransitionException>(() => ClaimRemedy.SetByPolicy(
            Guid.CreateVersion7(), Tenant, Guid.CreateVersion7(), Version, RemedyScenario.CoveredDefect,
            RemedyType.Repair, [RemedyType.Repair, RemedyType.Replacement], T0));
    }

    [Fact]
    public void Decide_AllowedRemedy_MovesToInProgress_AndOutsideAllowedIsRefused()
    {
        var remedy = Choice(RemedyType.Repair, RemedyType.Replacement);
        Assert.Equal(RemedyState.AwaitingDecision, remedy.State);
        Assert.Null(remedy.Remedy);

        Assert.Throws<InvalidClaimTransitionException>(() => remedy.Decide(RemedyType.Refund, T0));

        remedy.Decide(RemedyType.Replacement, T0.AddHours(1));
        Assert.Equal(RemedyState.InProgress, remedy.State);
        Assert.Equal(RemedyType.Replacement, remedy.Remedy);
        Assert.Null(remedy.PendingReason);
        Assert.Throws<InvalidClaimTransitionException>(() => remedy.Decide(RemedyType.Repair, T0));
    }

    [Fact]
    public void ReturnToDecision_FromInProgress_ClearsRemedy()
    {
        var remedy = Choice(RemedyType.Repair, RemedyType.Replacement);
        remedy.Decide(RemedyType.Repair, T0);

        remedy.ReturnToDecision(
            Version, RemedyScenario.RepairFailed, [RemedyType.Replacement, RemedyType.Refund],
            RemedyPendingReason.RepairFailedLimit, T0.AddDays(3));

        Assert.Equal(RemedyState.AwaitingDecision, remedy.State);
        Assert.Null(remedy.Remedy);
        Assert.Equal(RemedyPendingReason.RepairFailedLimit, remedy.PendingReason);
        Assert.Equal([RemedyType.Replacement, RemedyType.Refund], remedy.AllowedRemedies);
        Assert.Throws<InvalidClaimTransitionException>(() => remedy.Decide(RemedyType.Repair, T0));
    }

    [Fact]
    public void Complete_IsFinal_AndRequiresCurrentRemedy()
    {
        var remedy = Choice(RemedyType.Repair, RemedyType.Refund);
        Assert.Throws<InvalidClaimTransitionException>(() => remedy.Complete(RemedyType.Repair, T0));

        remedy.Decide(RemedyType.Repair, T0);
        Assert.Throws<InvalidClaimTransitionException>(() => remedy.Complete(RemedyType.Refund, T0));

        remedy.Complete(RemedyType.Repair, T0.AddDays(5));
        Assert.Equal(RemedyState.Completed, remedy.State);
        Assert.Throws<InvalidClaimTransitionException>(() => remedy.Complete(RemedyType.Repair, T0));
        Assert.Throws<InvalidClaimTransitionException>(() => remedy.ReturnToDecision(
            Version, RemedyScenario.RepairFailed, [RemedyType.Refund], RemedyPendingReason.RepairFailedLimit, T0));
    }

    [Fact]
    public void RemedyDecision_Decided_RefusesRemedyOutsideAllowed_AndChecksActorAndReason()
    {
        RemedyDecision Decided(RemedyType remedy, RemedyDecidedBy by, string actor, string? reason) => RemedyDecision.Decided(
            Guid.CreateVersion7(), Tenant, Guid.CreateVersion7(), 2, RemedyScenario.CoveredDefect, Version, "coveredDefect",
            [RemedyType.Repair, RemedyType.Replacement], remedy, by, actor, reason, 0, null, T0);

        Assert.Throws<InvalidClaimTransitionException>(() => Decided(RemedyType.Refund, RemedyDecidedBy.Reviewer, "sub-1", null));
        Assert.Throws<ArgumentException>(() => Decided(RemedyType.Repair, RemedyDecidedBy.Policy, "sub-1", null));
        Assert.Throws<ArgumentException>(() => Decided(RemedyType.Repair, RemedyDecidedBy.Reviewer, "sub-1", "short"));

        var row = Decided(RemedyType.Replacement, RemedyDecidedBy.Reviewer, "sub-1", "Customer needs the unit urgently.");
        Assert.Equal(RemedyDecisionKind.Decided, row.Kind);
        Assert.Equal("sub-1", row.Actor);
    }

    [Fact]
    public void RemedyDecision_HasNoPublicSetters()
    {
        Assert.All(
            typeof(RemedyDecision).GetProperties(),
            property => Assert.False(property.SetMethod?.IsPublic ?? false, property.Name));
    }

    [Fact]
    public void RemedyCompletion_RefundNeedsReference_AndRepairHasNoRow()
    {
        var refund = Choice(RemedyType.Refund, RemedyType.Repair);
        refund.Decide(RemedyType.Refund, T0);
        var today = DateOnly.FromDateTime(T0.UtcDateTime);

        Assert.Throws<ArgumentException>(() => RemedyCompletion.Record(Guid.CreateVersion7(), refund, today, null, null, "sub-2", T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RemedyCompletion.Record(
            Guid.CreateVersion7(), refund, today.AddDays(1), "RF-1", null, "sub-2", T0));

        var completion = RemedyCompletion.Record(Guid.CreateVersion7(), refund, today, "RF-1", Guid.CreateVersion7(), "sub-2", T0);
        Assert.Equal(RemedyType.Refund, completion.Remedy);
        Assert.Equal(refund.ClaimId, completion.ClaimId);

        var repair = Choice(RemedyType.Repair, RemedyType.Refund);
        repair.Decide(RemedyType.Repair, T0);
        Assert.Throws<InvalidClaimTransitionException>(() => RemedyCompletion.Record(
            Guid.CreateVersion7(), repair, today, null, null, "sub-2", T0));
    }
}
