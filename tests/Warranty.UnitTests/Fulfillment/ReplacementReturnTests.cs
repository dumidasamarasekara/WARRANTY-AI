using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Fulfillment;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Rules;
using Warranty.UnitTests.Domain;

namespace Warranty.UnitTests.Fulfillment;

/// <summary>
/// The faulty-product return of a replacement (003 T334; User Story 2, FR-018 – FR-021, FR-024, research R5, R9; quickstart
/// X6 – X9). Under <see cref="ReplacementReturnRule.BeforeReservation"/> the product is returned first: nothing can be reserved
/// or handed over before, a return whose serial matches the claim's under the 001 FR-016 normalisation
/// (<see cref="EvidenceMatchRules.IdentifiersMatch"/>) moves the replacement on, and one that does not is kept as a
/// <see cref="ReturnState.Mismatch"/> that blocks the reservation and the hand-over under either rule until a claims reviewer —
/// never an agent or auditor, and never the reviewer who submitted the claim — accepts it with a reason. A 002 repair whose centre
/// holds the faulty product (<see cref="Repair.HandoverHoldingProduct"/>) satisfies both rules: the replacement starts with a
/// return recorded by the system, referring to the repair hand-over, and the hand-over to the customer asks for no return.
/// <para>
/// Skips: the return before reservation, the mismatch and <see cref="Replacement.AcceptReturn"/> are T336;
/// <see cref="Replacement.RecordHeldReturn"/> and <see cref="RecordReplacementEvent"/>'s <c>ReturnRecorded</c> before reservation,
/// mismatch and <c>ReturnAccepted</c> (claims reviewer only, <c>SELF_REVIEW_REFUSED</c>) are T337, which merges after T336. The
/// status rules that already refuse a reservation, a hand-over or a return without a location, the serial comparison itself and
/// the held-product detection on <see cref="Repair"/> are on master and run unskipped. The review queue item for a mismatch
/// (<c>RETURN_MISMATCH</c>) is tested with T338.
/// </para>
/// </summary>
public sealed class ReplacementReturnTests
{
    private const string AgentActor = "agent-sub-t334";
    private const string ReviewerActor = "reviewer-sub-t334";
    private const string AuditorActor = "auditor-sub-t334";
    private const string ReviewerName = "Borealis Reviewer";
    private const string StaffNote = "Returned in its original box; staff-only note.";
    private const string AcceptReason = "The claim's serial had a typo; the returned tablet is the claimed one.";
    private const string ClaimSerial = "BS11-24-0307";
    private const string WrongSerial = "BS11-24-0308";
    private const string UnitSerial = "BS11-R-7001";
    private const uint RowVersion = 3341;
    private const string ValidIfMatch = "\"3341\"";

    private static readonly Guid Tenant = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid ClaimId = Guid.Parse("0199c000-0000-7000-8000-000000000334");
    private static readonly Guid PolicyId = Guid.Parse("0199c000-0000-7000-8000-0000000003a4");
    private static readonly Guid VersionId = Guid.Parse("0199c000-0000-7000-8000-0000000003b4");
    private static readonly Guid CustomerId = Guid.Parse("0199c000-0000-7000-8000-0000000003d4");
    private static readonly Guid ClaimedProduct = Guid.Parse("0199c000-0000-7000-8000-0000000003e4");
    private static readonly Guid SuggestedLocation = Guid.Parse("33333333-3333-7333-8333-333333333341");
    private static readonly Guid RepairCentre = Guid.Parse("33333333-3333-7333-8333-333333333342");
    private static readonly Guid OtherRepairCentre = Guid.Parse("33333333-3333-7333-8333-333333333343");
    private static readonly DateOnly PurchaseDate = new(2026, 3, 2);
    private static readonly DateTimeOffset SubmittedAt = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly FirstDay = new(2026, 10, 2);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 14, 30, 0, TimeSpan.Zero);

    private readonly ITenantContext _tenant = Substitute.For<ITenantContext>();
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly ICatalogRepository _catalog = Substitute.For<ICatalogRepository>();
    private readonly IRemedyRepository _remedies = Substitute.For<IRemedyRepository>();
    private readonly IReplacementRepository _replacements = Substitute.For<IReplacementRepository>();
    private readonly IReplacementSerialRegistry _serials = Substitute.For<IReplacementSerialRegistry>();
    private readonly IPolicyRepository _policies = Substitute.For<IPolicyRepository>();
    private readonly IServiceNetwork _network = Substitute.For<IServiceNetwork>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IDecisionTrailWriter _trail = Substitute.For<IDecisionTrailWriter>();
    private readonly ISecurityEventWriter _securityEvents = Substitute.For<ISecurityEventWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _time = new(Now);

    public ReplacementReturnTests()
    {
        _tenant.IsResolved.Returns(true);
        _tenant.IsSystem.Returns(false);
        _tenant.TenantId.Returns(Tenant);
        UsePrincipal(AgentActor, Principals.ClaimsAgentRole);

        _unitOfWork.ExecuteInTransactionAsync(default!, default)
            .ReturnsForAnyArgs(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        _catalog.GetProductAsync(ClaimedProduct, Arg.Any<CancellationToken>())
            .Returns(Product.Create(ClaimedProduct, Tenant, "BOR-SLATE11", "Borealis Slate 11", "tablet", 450m, "EUR"));

        var terms = new CoverageTerms(
            new Dictionary<Region, int> { [Region.EU] = 24 }, new Dictionary<string, int>(), AccidentalDamageTerms.NotCovered,
            [ExclusionCode.LiquidDamage]);
        _policies.GetVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(PolicyVersion.Create(
            VersionId, Tenant, PolicyId, 1, new DateOnly(2025, 1, 1), null, [Region.EU], [], terms,
            "tenant/policies/BOR-WP-v1.md", "checksum", RemedyRulesContract.BorealisWithReplacement));
        _policies.GetPolicyAsync(PolicyId, Arg.Any<CancellationToken>())
            .Returns(WarrantyPolicy.Create(PolicyId, Tenant, "BOR-WP", "Borealis Care Warranty"));
    }

    /// <summary>Where a 002 repair stands when its remedy changes to Replacement (research R9).</summary>
    public enum RepairStage
    {
        /// <summary>Suggested to a centre, not handed over yet.</summary>
        NotHandedOver,

        /// <summary>Handed over, attempt 1 open.</summary>
        AtRepairCentre,

        /// <summary>Attempt 1 failed at the centre.</summary>
        FailedOnce,

        /// <summary>Attempt 1 failed, attempt 2 started with the product kept at the centre (the repair is at the centre again).</summary>
        NewAttemptAtCentre,

        /// <summary>Both attempts failed with the product kept at the centre (quickstart X9, 002 R8).</summary>
        FailedTwiceAtCentre,

        /// <summary>The estimate was above the economic limit (Borealis's way to a Replacement).</summary>
        NotEconomical,

        /// <summary>Attempt 1 failed and the product went back to the customer for attempt 2.</summary>
        ReturnedToCustomerAfterFailure,

        /// <summary>As <see cref="ReturnedToCustomerAfterFailure"/>, then handed over to the centre again.</summary>
        HandedOverAgainAfterReturn,

        /// <summary>Transferred to another centre after the hand-over, not handed over there yet.</summary>
        TransferredAfterHandover,

        /// <summary>Repaired and waiting for the customer.</summary>
        ReadyForCollection,

        /// <summary>Repaired and collected by the customer.</summary>
        Collected,
    }

    /// <summary>Serials a location may report for the claim's <see cref="ClaimSerial"/>, and whether they match (001 FR-016).</summary>
    public static TheoryData<string, bool> SerialsSeen => new()
    {
        { "BS11-24-0307", true },
        { "bs11-24-0307", true },
        { " BS11 24 0307 ", true },
        { "BS11_24.0307", true },
        { "bs1124-0307", true },
        { "BS11-24-0308", false },
        { "BS11-24-03070", false },
        { "BS11-24-O307", false },
        { "BS12-24-0307", false },
    };

    /// <summary>Whether the centre of a repair at each <see cref="RepairStage"/> holds the faulty product (research R9).</summary>
    public static TheoryData<RepairStage, bool> RepairStages => new()
    {
        { RepairStage.NotHandedOver, false },
        { RepairStage.AtRepairCentre, true },
        { RepairStage.FailedOnce, true },
        { RepairStage.NewAttemptAtCentre, true },
        { RepairStage.FailedTwiceAtCentre, true },
        { RepairStage.NotEconomical, true },
        { RepairStage.ReturnedToCustomerAfterFailure, false },
        { RepairStage.HandedOverAgainAfterReturn, true },
        { RepairStage.TransferredAfterHandover, false },
        { RepairStage.ReadyForCollection, false },
        { RepairStage.Collected, false },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Return before reservation (FR-018; X6, X7) ─────────────────────────────────────────────

    [Fact]
    public void Before_the_return_no_unit_can_be_reserved_and_nothing_handed_over()
    {
        var replacement = AwaitingReturn();

        foreach (var role in new[] { StaffRole.ClaimsAgent, StaffRole.ClaimsReviewer })
        {
            var allowed = replacement.AllowedEvents(role);
            allowed.ShouldContain(ReplacementEventType.ReturnRecorded);
            allowed.ShouldNotContain(ReplacementEventType.UnitReserved);
            allowed.ShouldNotContain(ReplacementEventType.HandedToCustomer);
        }

        Should.Throw<InvalidOperationException>(() => replacement.ReserveUnit(ClaimedProduct, UnitSerial, Report(1, StaffRole.ClaimsAgent)))
            .Message.ShouldContain(nameof(ReplacementStatus.AwaitingReturn));
        Should.Throw<InvalidOperationException>(
            () => replacement.HandToCustomer(FirstDay.AddDays(1), returnConfirmed: true, Report(1, StaffRole.ClaimsReviewer)));

        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingReturn, ReturnState.NotYetReturned));
        replacement.Units.ShouldBeEmpty();
        replacement.Return.ShouldBeNull();
        replacement.Events.Count.ShouldBe(1);
    }

    [Fact]
    public void A_return_needs_an_active_location()
    {
        var replacement = Create(ReplacementReturnRule.BeforeReservation, ReplacementStatus.AwaitingReturn, suggestedLocation: null);

        replacement.AllowedEvents(StaffRole.ClaimsAgent).ShouldNotContain(ReplacementEventType.ReturnRecorded);
        Should.Throw<InvalidOperationException>(
            () => replacement.RecordReturn(FirstDay, ClaimSerial, serialMatches: true, Report(1, StaffRole.ClaimsAgent)));

        replacement.Return.ShouldBeNull();
        replacement.Events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_return_needs_the_serial_seen_on_the_product(string? serialSeen)
    {
        var replacement = AwaitingReturn();

        Should.Throw<ArgumentException>(
            () => replacement.RecordReturn(FirstDay, serialSeen!, serialMatches: false, Report(1, StaffRole.ClaimsAgent)));

        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingReturn, ReturnState.NotYetReturned));
        replacement.Return.ShouldBeNull();
        replacement.Events.Count.ShouldBe(1);
    }

    [Fact(Skip = "Pending T336")]
    public void A_matching_return_before_reservation_is_recorded_at_the_active_location_and_awaits_a_unit()
    {
        var replacement = AwaitingReturn();

        var recorded = replacement.RecordReturn(FirstDay, " bs11 24 0307 ", serialMatches: true, Report(1, StaffRole.ClaimsAgent, StaffNote));

        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingUnit, ReturnState.Returned));
        var faultyReturn = replacement.Return.ShouldNotBeNull();
        (faultyReturn.ReturnedOn, faultyReturn.ServiceCenterId, faultyReturn.SerialSeen, faultyReturn.SerialMatches).ShouldBe(
            (FirstDay, (Guid?)SuggestedLocation, "bs11 24 0307", true));
        (faultyReturn.IsHeldByRepairCentre, faultyReturn.Note, faultyReturn.RecordedBy).ShouldBe((false, StaffNote, AgentActor));
        (recorded.Type, recorded.FromStatus, recorded.ToStatus, recorded.Seq, recorded.Actor, recorded.Source).ShouldBe(
            (ReplacementEventType.ReturnRecorded, ReplacementStatus.AwaitingReturn, ReplacementStatus.AwaitingUnit, 2, AgentActor,
                ReportSource.Phone));
        var payload = Payload(recorded);
        payload.GetProperty("serialMatches").GetBoolean().ShouldBeTrue();
        payload.GetProperty("returnState").GetString().ShouldBe(nameof(ReturnState.Returned));
        replacement.AllowedEvents(StaffRole.ClaimsAgent).ShouldContain(ReplacementEventType.UnitReserved);
    }

    [Fact(Skip = "Pending T336")]
    public void Under_BeforeReservation_the_return_is_recorded_once_and_the_hand_over_asks_for_no_return()
    {
        var replacement = AwaitingReturn();
        replacement.RecordReturn(FirstDay, ClaimSerial, serialMatches: true, Report(1, StaffRole.ClaimsAgent));

        replacement.AllowedEvents(StaffRole.ClaimsReviewer).ShouldNotContain(ReplacementEventType.ReturnRecorded);
        Should.Throw<InvalidOperationException>(
            () => replacement.RecordReturn(FirstDay.AddDays(1), ClaimSerial, serialMatches: true, Report(2, StaffRole.ClaimsAgent)));

        replacement.ReserveUnit(ClaimedProduct, UnitSerial, Report(2, StaffRole.ClaimsAgent));
        var handedOver = replacement.HandToCustomer(FirstDay.AddDays(3), returnConfirmed: false, Report(3, StaffRole.ClaimsAgent));

        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.HandedToCustomer, ReturnState.Returned));
        Payload(handedOver).GetProperty("returnConfirmed").GetBoolean().ShouldBeFalse();
        replacement.Events.Select(e => e.Type).ShouldBe(
        [
            ReplacementEventType.LocationSuggested, ReplacementEventType.ReturnRecorded, ReplacementEventType.UnitReserved,
            ReplacementEventType.HandedToCustomer,
        ]);
    }

    // ── The serial check (FR-019, 001 FR-016) ──────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(SerialsSeen))]
    public void The_returned_serial_is_compared_with_the_claims_serial_under_the_001_FR_016_normalisation(string serialSeen, bool matches)
        => (EvidenceMatchRules.IdentifiersMatch(ClaimSerial, serialSeen) == EvidenceMatch.Match).ShouldBe(matches);

    [Theory(Skip = "Pending T336")]
    [MemberData(nameof(SerialsSeen))]
    public void The_serial_check_decides_whether_the_return_before_reservation_moves_the_replacement_on(string serialSeen, bool matches)
    {
        var replacement = AwaitingReturn();
        var serialMatches = EvidenceMatchRules.IdentifiersMatch(ClaimSerial, serialSeen) == EvidenceMatch.Match;

        replacement.RecordReturn(FirstDay, serialSeen, serialMatches, Report(1, StaffRole.ClaimsAgent));

        (replacement.Status, replacement.ReturnState, replacement.Return.ShouldNotBeNull().SerialMatches).ShouldBe(
            matches
                ? (ReplacementStatus.AwaitingUnit, ReturnState.Returned, true)
                : (ReplacementStatus.AwaitingReturn, ReturnState.Mismatch, false));
    }

    // ── A mismatch blocks the reservation and the hand-over (FR-019; X8) ───────────────────────

    [Fact(Skip = "Pending T336")]
    public void A_mismatch_before_reservation_is_kept_and_the_claim_keeps_awaiting_the_return()
    {
        var replacement = AwaitingReturn();

        var recorded = replacement.RecordReturn(FirstDay, WrongSerial, serialMatches: false, Report(1, StaffRole.ClaimsAgent));

        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingReturn, ReturnState.Mismatch));
        var faultyReturn = replacement.Return.ShouldNotBeNull();
        (faultyReturn.ServiceCenterId, faultyReturn.SerialSeen, faultyReturn.SerialMatches).ShouldBe(
            ((Guid?)SuggestedLocation, WrongSerial, false));
        (recorded.Type, recorded.FromStatus, recorded.ToStatus).ShouldBe(
            (ReplacementEventType.ReturnRecorded, ReplacementStatus.AwaitingReturn, ReplacementStatus.AwaitingReturn));
        var payload = Payload(recorded);
        payload.GetProperty("serialMatches").GetBoolean().ShouldBeFalse();
        payload.GetProperty("returnState").GetString().ShouldBe(nameof(ReturnState.Mismatch));

        var agentEvents = replacement.AllowedEvents(StaffRole.ClaimsAgent);
        agentEvents.ShouldNotContain(ReplacementEventType.UnitReserved);
        agentEvents.ShouldNotContain(ReplacementEventType.ReturnRecorded);
        agentEvents.ShouldNotContain(ReplacementEventType.ReturnAccepted);
        replacement.AllowedEvents(StaffRole.ClaimsReviewer).ShouldContain(ReplacementEventType.ReturnAccepted);
        Should.Throw<InvalidOperationException>(() => replacement.ReserveUnit(ClaimedProduct, UnitSerial, Report(2, StaffRole.ClaimsAgent)));
        replacement.Units.ShouldBeEmpty();
    }

    [Fact(Skip = "Pending T336")]
    public void A_mismatch_at_collection_blocks_the_reservation()
    {
        var replacement = AwaitingUnitAtCollection();

        replacement.RecordReturn(FirstDay, WrongSerial, serialMatches: false, Report(1, StaffRole.ClaimsAgent));

        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingUnit, ReturnState.Mismatch));
        replacement.AllowedEvents(StaffRole.ClaimsReviewer).ShouldNotContain(ReplacementEventType.UnitReserved);
        Should.Throw<InvalidOperationException>(() => replacement.ReserveUnit(ClaimedProduct, UnitSerial, Report(2, StaffRole.ClaimsReviewer)));
        replacement.Units.ShouldBeEmpty();
    }

    [Fact(Skip = "Pending T336")]
    public void A_mismatch_at_collection_blocks_the_hand_over_even_with_the_return_confirmed()
    {
        var replacement = ReadyForCollectionMismatch();
        var eventCount = replacement.Events.Count;

        replacement.AllowedEvents(StaffRole.ClaimsAgent).ShouldNotContain(ReplacementEventType.HandedToCustomer);
        Should.Throw<InvalidOperationException>(
            () => replacement.HandToCustomer(FirstDay.AddDays(3), returnConfirmed: true, Report(3, StaffRole.ClaimsAgent)));

        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.ReadyForCollection, ReturnState.Mismatch));
        replacement.CurrentUnit.ShouldNotBeNull().Result.ShouldBe(ReplacementUnitResult.Reserved);
        replacement.Events.Count.ShouldBe(eventCount);
    }

    // ── ReturnAccepted: claims reviewer only, with a reason (FR-024, research R5; X8) ──────────

    [Theory(Skip = "Pending T336")]
    [InlineData(StaffRole.ClaimsAgent)]
    [InlineData(StaffRole.Auditor)]
    public void Only_a_claims_reviewer_accepts_a_returned_product_that_does_not_match(StaffRole role)
    {
        var replacement = AwaitingReturnMismatch();
        var eventCount = replacement.Events.Count;

        replacement.AllowedEvents(role).ShouldNotContain(ReplacementEventType.ReturnAccepted);
        Should.Throw<UnauthorizedAccessException>(() => replacement.AcceptReturn(AcceptReason, Report(2, role)));

        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingReturn, ReturnState.Mismatch));
        replacement.Events.Count.ShouldBe(eventCount);
    }

    [Fact(Skip = "Pending T336")]
    public void A_reviewer_accepting_the_returned_product_before_reservation_lets_the_replacement_go_on()
    {
        var replacement = AwaitingReturnMismatch();

        var accepted = replacement.AcceptReturn(AcceptReason, Report(2, StaffRole.ClaimsReviewer));

        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingUnit, ReturnState.Accepted));
        (accepted.Type, accepted.FromStatus, accepted.ToStatus, accepted.Actor).ShouldBe(
            (ReplacementEventType.ReturnAccepted, ReplacementStatus.AwaitingReturn, ReplacementStatus.AwaitingUnit, ReviewerActor));
        Payload(accepted).GetProperty("reason").GetString().ShouldBe(AcceptReason);
        replacement.Return.ShouldNotBeNull().SerialMatches.ShouldBeFalse("the check result is kept; only its consequence is accepted");
        replacement.AllowedEvents(StaffRole.ClaimsReviewer).ShouldNotContain(ReplacementEventType.ReturnAccepted);

        replacement.ReserveUnit(ClaimedProduct, UnitSerial, Report(3, StaffRole.ClaimsAgent));
        replacement.HandToCustomer(FirstDay.AddDays(4), returnConfirmed: false, Report(4, StaffRole.ClaimsAgent));
        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.HandedToCustomer, ReturnState.Accepted));
    }

    [Fact(Skip = "Pending T336")]
    public void Accepting_the_returned_product_at_collection_keeps_the_status_and_allows_the_hand_over()
    {
        var replacement = ReadyForCollectionMismatch();

        var accepted = replacement.AcceptReturn(AcceptReason, Report(3, StaffRole.ClaimsReviewer));

        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.ReadyForCollection, ReturnState.Accepted));
        (accepted.FromStatus, accepted.ToStatus).ShouldBe((ReplacementStatus.ReadyForCollection, ReplacementStatus.ReadyForCollection));
        replacement.AllowedEvents(StaffRole.ClaimsAgent).ShouldContain(ReplacementEventType.HandedToCustomer);

        replacement.HandToCustomer(FirstDay.AddDays(4), returnConfirmed: false, Report(4, StaffRole.ClaimsAgent));
        replacement.Status.ShouldBe(ReplacementStatus.HandedToCustomer);
    }

    [Theory(Skip = "Pending T336")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Accepting_the_returned_product_needs_a_reason(string? reason)
    {
        var replacement = AwaitingReturnMismatch();
        var eventCount = replacement.Events.Count;

        Should.Throw<ArgumentException>(() => replacement.AcceptReturn(reason!, Report(2, StaffRole.ClaimsReviewer)));

        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingReturn, ReturnState.Mismatch));
        replacement.Events.Count.ShouldBe(eventCount);
    }

    [Fact(Skip = "Pending T336")]
    public void Without_a_mismatch_there_is_no_return_to_accept()
    {
        var returned = AwaitingReturn();
        returned.RecordReturn(FirstDay, ClaimSerial, serialMatches: true, Report(1, StaffRole.ClaimsAgent));

        foreach (var replacement in new[] { AwaitingReturn(), AwaitingUnitAtCollection(), returned })
        {
            var (status, eventCount) = (replacement.Status, replacement.Events.Count);

            replacement.AllowedEvents(StaffRole.ClaimsReviewer).ShouldNotContain(ReplacementEventType.ReturnAccepted);
            Should.Throw<InvalidOperationException>(() => replacement.AcceptReturn(AcceptReason, Report(2, StaffRole.ClaimsReviewer)));

            replacement.Status.ShouldBe(status);
            replacement.Events.Count.ShouldBe(eventCount);
        }
    }

    // ── Product held by a repair centre (FR-020, research R9; X9) ──────────────────────────────

    [Theory]
    [MemberData(nameof(RepairStages))]
    public void A_repair_centre_holds_the_faulty_product_from_the_hand_over_until_it_leaves_the_centre(RepairStage stage, bool held)
    {
        var repair = RepairAt(stage);

        var handover = repair.HandoverHoldingProduct();

        if (held)
        {
            handover.ShouldNotBeNull().ShouldBeSameAs(repair.ActiveHandover);
            handover.HandedOverOn.ShouldNotBeNull();
        }
        else
        {
            handover.ShouldBeNull();
        }
    }

    [Fact(Skip = "Pending T337")]
    public void A_product_held_by_a_repair_centre_is_recorded_as_returned_by_the_system_referring_to_the_repair_hand_over()
    {
        var handover = RepairAt(RepairStage.FailedTwiceAtCentre).HandoverHoldingProduct().ShouldNotBeNull();
        var replacement = Create(ReplacementReturnRule.BeforeReservation, ReplacementStatus.AwaitingUnit, SuggestedLocation);

        var recorded = replacement.RecordHeldReturn(handover.Id, handover.HandedOverOn!.Value, CreatedAt);

        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingUnit, ReturnState.HeldByRepairCentre));
        var faultyReturn = replacement.Return.ShouldNotBeNull();
        (faultyReturn.IsHeldByRepairCentre, faultyReturn.RepairHandoverId, faultyReturn.ReturnedOn).ShouldBe(
            (true, (Guid?)handover.Id, handover.HandedOverOn.Value));
        (faultyReturn.ServiceCenterId, faultyReturn.SerialSeen, faultyReturn.SerialMatches).ShouldBe(((Guid?)null, (string?)null, true));
        faultyReturn.RecordedBy.ShouldBe(ReplacementEvent.SystemActor);
        (recorded.Type, recorded.FromStatus, recorded.ToStatus, recorded.Seq).ShouldBe(
            (ReplacementEventType.ReturnRecorded, ReplacementStatus.AwaitingUnit, ReplacementStatus.AwaitingUnit, 2));
        (recorded.Actor, recorded.Source).ShouldBe((ReplacementEvent.SystemActor, ReportSource.System));
        Payload(recorded).GetProperty("repairHandoverId").GetGuid().ShouldBe(handover.Id);

        var allowed = replacement.AllowedEvents(StaffRole.ClaimsAgent);
        allowed.ShouldNotContain(ReplacementEventType.ReturnRecorded);
        allowed.ShouldContain(ReplacementEventType.UnitReserved);
        Should.Throw<InvalidOperationException>(() => replacement.RecordHeldReturn(handover.Id, handover.HandedOverOn.Value, CreatedAt));
    }

    [Fact(Skip = "Pending T337")]
    public void Under_AtCollection_a_product_held_by_a_repair_centre_needs_no_return_confirmation_at_the_hand_over()
    {
        var handover = RepairAt(RepairStage.FailedTwiceAtCentre).HandoverHoldingProduct().ShouldNotBeNull();
        var replacement = AwaitingUnitAtCollection();
        replacement.RecordHeldReturn(handover.Id, handover.HandedOverOn!.Value, CreatedAt);
        replacement.ReserveUnit(ClaimedProduct, UnitSerial, Report(1, StaffRole.ClaimsAgent));

        replacement.AllowedEvents(StaffRole.ClaimsAgent).ShouldNotContain(ReplacementEventType.ReturnRecorded);
        var handedOver = replacement.HandToCustomer(FirstDay.AddDays(2), returnConfirmed: false, Report(2, StaffRole.ClaimsAgent));

        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.HandedToCustomer, ReturnState.HeldByRepairCentre));
        Payload(handedOver).GetProperty("returnConfirmed").GetBoolean().ShouldBeFalse();
    }

    // ── Through the use case (RecordReplacementEvent; X6 – X8, FR-024) ─────────────────────────

    [Fact(Skip = "Pending T337")]
    public async Task A_return_before_reservation_with_the_claims_serial_spelled_differently_is_recorded_as_matching()
    {
        var (claim, replacement) = ArrangeAwaitingReturn();
        var events = CaptureEvents();

        var result = await Execute(claim, ReturnRecorded("bs11 24 0307"));

        result.ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingUnit, ReturnState.Returned));
        replacement.Return.ShouldNotBeNull().SerialMatches.ShouldBeTrue();
        events.ShouldHaveSingleItem().Type.ShouldBe(ReplacementEventType.ReturnRecorded);
        await _trail.ReceivedWithAnyArgs(1).AppendAsync(default, default, default!, default!, default, Ct);
    }

    [Fact(Skip = "Pending T337")]
    public async Task A_return_with_another_serial_is_recorded_as_a_mismatch_and_keeps_awaiting_the_return()
    {
        var (claim, replacement) = ArrangeAwaitingReturn();

        var result = await Execute(claim, ReturnRecorded(WrongSerial));

        result.ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingReturn, ReturnState.Mismatch));
        replacement.Return.ShouldNotBeNull().SerialMatches.ShouldBeFalse();
        _notifications.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact(Skip = "Pending T337")]
    public async Task A_claims_agent_cannot_accept_a_returned_product()
    {
        var (claim, replacement) = ArrangeAwaitingReturnMismatch();

        var result = await Execute(claim, ReturnAccepted(AcceptReason));

        result.ShouldBeOfType<RecordReplacementEventResult.Forbidden>();
        ShouldBeUnchanged(replacement);
    }

    [Fact(Skip = "Pending T337")]
    public async Task The_reviewer_who_submitted_the_claim_cannot_accept_its_returned_product()
    {
        UsePrincipal(ReviewerActor, Principals.ClaimsReviewerRole);
        var (claim, replacement) = ArrangeAwaitingReturnMismatch(submittedBy: ReviewerActor);

        var result = await Execute(claim, ReturnAccepted(AcceptReason));

        result.ShouldBeOfType<RecordReplacementEventResult.SelfReviewRefused>();
        await _securityEvents.Received(1).RecordAsync(
            SecurityEventKind.SelfReviewRefused, ReviewerName, claim.Id.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
        ShouldBeUnchanged(replacement);
    }

    [Fact(Skip = "Pending T337")]
    public async Task Another_reviewer_accepts_the_returned_product_with_a_reason()
    {
        UsePrincipal(ReviewerActor, Principals.ClaimsReviewerRole);
        var (claim, replacement) = ArrangeAwaitingReturnMismatch();
        var events = CaptureEvents();

        var result = await Execute(claim, ReturnAccepted(AcceptReason));

        result.ShouldBeOfType<RecordReplacementEventResult.Recorded>();
        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingUnit, ReturnState.Accepted));
        var accepted = events.ShouldHaveSingleItem();
        (accepted.Type, accepted.Actor).ShouldBe((ReplacementEventType.ReturnAccepted, ReviewerActor));
        _securityEvents.ReceivedCalls().ShouldBeEmpty();
        await _unitOfWork.ReceivedWithAnyArgs(1).ExecuteInTransactionAsync(default!, Ct);
    }

    [Theory(Skip = "Pending T337")]
    [InlineData(null)]
    [InlineData("too short")]
    public async Task Accepting_a_returned_product_needs_a_reason_of_10_to_1000_characters(string? reason)
    {
        UsePrincipal(ReviewerActor, Principals.ClaimsReviewerRole);
        var (claim, replacement) = ArrangeAwaitingReturnMismatch();

        var result = await Execute(claim, ReturnAccepted(reason));

        result.ShouldBeOfType<RecordReplacementEventResult.Invalid>().Errors.Keys.ShouldContain("reason");
        ShouldBeUnchanged(replacement);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static string ActorOf(StaffRole role) => role switch
    {
        StaffRole.ClaimsAgent => AgentActor,
        StaffRole.ClaimsReviewer => ReviewerActor,
        _ => AuditorActor,
    };

    /// <summary>A report by phone with <paramref name="role"/>, <paramref name="day"/> days after the first day.</summary>
    private static ReplacementReport Report(int day, StaffRole role, string? note = null)
    {
        var at = new DateTimeOffset(FirstDay.AddDays(day).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        return new ReplacementReport(at, ReportSource.Phone, ActorOf(role), role, note, at.AddMinutes(5));
    }

    private static Replacement Create(ReplacementReturnRule returnRule, ReplacementStatus initialStatus, Guid? suggestedLocation)
        => Replacement.Create(
            Guid.CreateVersion7(), Tenant, ClaimId, VersionId, ReplacementProductRule.SameModel, returnRule,
            minimumCoverMonths: 6, initialStatus, suggestedLocation, CreatedAt);

    /// <summary>Borealis's rule (X6): returned before reservation, awaiting the return at the suggested location.</summary>
    private static Replacement AwaitingReturn()
        => Create(ReplacementReturnRule.BeforeReservation, ReplacementStatus.AwaitingReturn, SuggestedLocation);

    /// <summary>As <see cref="AwaitingReturn"/>, with a returned product whose serial did not match (X8).</summary>
    private static Replacement AwaitingReturnMismatch()
    {
        var replacement = AwaitingReturn();
        replacement.RecordReturn(FirstDay, WrongSerial, serialMatches: false, Report(1, StaffRole.ClaimsAgent));
        return replacement;
    }

    /// <summary>Aurora's rule: returned at collection, awaiting a unit at the suggested location.</summary>
    private static Replacement AwaitingUnitAtCollection()
        => Create(ReplacementReturnRule.AtCollection, ReplacementStatus.AwaitingUnit, SuggestedLocation);

    /// <summary>As <see cref="AwaitingUnitAtCollection"/>, a unit reserved on day 1 and a non-matching return on day 2.</summary>
    private static Replacement ReadyForCollectionMismatch()
    {
        var replacement = AwaitingUnitAtCollection();
        replacement.ReserveUnit(ClaimedProduct, UnitSerial, Report(1, StaffRole.ClaimsAgent));
        replacement.RecordReturn(FirstDay.AddDays(2), WrongSerial, serialMatches: false, Report(2, StaffRole.ClaimsAgent));
        return replacement;
    }

    /// <summary>A report to a 002 repair by phone, <paramref name="day"/> days after the first day.</summary>
    private static RepairReport RepairUpdate(int day)
    {
        var at = new DateTimeOffset(FirstDay.AddDays(day).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        return new RepairReport(at, ReportSource.Phone, AgentActor, null, at.AddMinutes(5));
    }

    /// <summary>A 002 repair at <see cref="RepairCentre"/> driven to <paramref name="stage"/> through its domain transitions.</summary>
    private static Repair RepairAt(RepairStage stage)
    {
        var repair = Repair.Create(Guid.CreateVersion7(), Tenant, ClaimId, RepairCentre, CreatedAt);
        if (stage == RepairStage.NotHandedOver)
        {
            return repair;
        }

        repair.RecordHandover(FirstDay, "JOB-3341", RepairUpdate(0));
        switch (stage)
        {
            case RepairStage.FailedOnce:
                repair.RecordFailure("The display still shows vertical lines.", RepairUpdate(2));
                break;
            case RepairStage.NewAttemptAtCentre:
                repair.RecordFailure("The display still shows vertical lines.", RepairUpdate(2));
                repair.StartNewAttempt(ProductLocation.AtCentre, RepairUpdate(3));
                break;
            case RepairStage.FailedTwiceAtCentre:
                repair.RecordFailure("The display still shows vertical lines.", RepairUpdate(2));
                repair.StartNewAttempt(ProductLocation.AtCentre, RepairUpdate(3));
                repair.RecordFailure("The replaced display panel fails too.", RepairUpdate(5));
                break;
            case RepairStage.NotEconomical:
                repair.RecordEstimate(350m, economicLimitAmount: 270m, RepairUpdate(2));
                break;
            case RepairStage.ReturnedToCustomerAfterFailure:
                repair.RecordFailure("The display still shows vertical lines.", RepairUpdate(2));
                repair.StartNewAttempt(ProductLocation.ReturnedToCustomer, RepairUpdate(3));
                break;
            case RepairStage.HandedOverAgainAfterReturn:
                repair.RecordFailure("The display still shows vertical lines.", RepairUpdate(2));
                repair.StartNewAttempt(ProductLocation.ReturnedToCustomer, RepairUpdate(3));
                repair.RecordHandover(FirstDay.AddDays(6), "JOB-3342", RepairUpdate(6));
                break;
            case RepairStage.TransferredAfterHandover:
                repair.Transfer(OtherRepairCentre, "The first centre has no spare display panels.", RepairUpdate(1));
                break;
            case RepairStage.ReadyForCollection:
                repair.MarkReady(RepairUpdate(2));
                break;
            case RepairStage.Collected:
                repair.MarkReady(RepairUpdate(2));
                repair.RecordCollection(RepairUpdate(3));
                break;
        }

        return repair;
    }

    private static JsonElement Payload(ReplacementEvent replacementEvent)
        => JsonDocument.Parse(replacementEvent.PayloadJson).RootElement.Clone();

    private void UsePrincipal(string subject, string role)
    {
        _tenant.PrincipalId.Returns(subject);
        _tenant.PrincipalName.Returns(role == Principals.ClaimsReviewerRole ? ReviewerName : "Borealis Agent");
        _tenant.Roles.Returns(new HashSet<string>(StringComparer.Ordinal) { role });
    }

    private Task<RecordReplacementEventResult> Execute(Claim claim, RecordReplacementEventCommand command)
        => new RecordReplacementEvent(
                _tenant, _claims, _catalog, _remedies, _replacements, _serials, _policies, _network, _notifications, _trail,
                _securityEvents, _unitOfWork, _time)
            .ExecuteAsync(command with { ClaimId = claim.Id }, Ct);

    private static RecordReplacementEventCommand ReturnRecorded(string serialSeen)
        => new(Guid.Empty, ValidIfMatch, ReplacementEventType.ReturnRecorded, ReportedOn(2), ReportSource.InPerson)
        {
            ReturnedOn = FirstDay.AddDays(2),
            SerialSeen = serialSeen,
        };

    private static RecordReplacementEventCommand ReturnAccepted(string? reason)
        => new(Guid.Empty, ValidIfMatch, ReplacementEventType.ReturnAccepted, ReportedOn(3), ReportSource.Other) { Reason = reason };

    /// <summary>10:00 UTC, <paramref name="day"/> days after <see cref="FirstDay"/> (after the set-up and before <see cref="Now"/>).</summary>
    private static DateTimeOffset ReportedOn(int day) => new(FirstDay.AddDays(day).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);

    /// <summary>
    /// An approved Borealis claim (serial <see cref="ClaimSerial"/>, submitted by <paramref name="submittedBy"/> through the agent
    /// portal) whose remedy Replacement is in progress and whose replacement awaits the return, behind the repositories.
    /// </summary>
    private (Claim Claim, Replacement Replacement) ArrangeAwaitingReturn(string submittedBy = "agent-sub-submitter")
    {
        var claim = Claim.Submit(
            Guid.CreateVersion7(), Tenant, ClaimReference.Generate(), ClaimChannel.AgentPortal, submittedBy, CustomerId,
            "claimant@synthetic-mail.test", null, "BOR-SLATE11", ClaimedProduct, ClaimSerial, PurchaseDate, "Online shop", 450m,
            Region.EU, "The display shows vertical lines and the touch input stops responding.", SubmittedAt);
        claim.StartEvaluation(SubmittedAt);
        claim.EscalateToReview(SubmittedAt);
        claim.FinalizeApproved("Your tablet is covered for manufacturing defects.", DecidedBy.Reviewer, SubmittedAt.AddDays(1));

        var remedy = ClaimRemedy.AwaitDecision(
            Guid.CreateVersion7(), Tenant, claim.Id, VersionId, RemedyScenario.RepairNotEconomical,
            [RemedyType.Refund, RemedyType.Replacement], RemedyPendingReason.RepairNotEconomical, CreatedAt);
        remedy.Decide(RemedyType.Replacement, CreatedAt);
        var replacement = Replacement.Create(
            Guid.CreateVersion7(), Tenant, claim.Id, VersionId, ReplacementProductRule.SameModel,
            ReplacementReturnRule.BeforeReservation, minimumCoverMonths: 6, ReplacementStatus.AwaitingReturn, SuggestedLocation, CreatedAt);

        _claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);
        _remedies.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(remedy);
        _replacements.GetByClaimAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(replacement);
        _replacements.GetRowVersionAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(RowVersion);
        return (claim, replacement);
    }

    /// <summary>As <see cref="ArrangeAwaitingReturn"/>, with a returned product whose serial did not match recorded on day 1.</summary>
    private (Claim Claim, Replacement Replacement) ArrangeAwaitingReturnMismatch(string submittedBy = "agent-sub-submitter")
    {
        var (claim, replacement) = ArrangeAwaitingReturn(submittedBy);
        replacement.RecordReturn(FirstDay.AddDays(1), WrongSerial, serialMatches: false, Report(1, StaffRole.ClaimsAgent));
        return (claim, replacement);
    }

    private List<ReplacementEvent> CaptureEvents()
    {
        var events = new List<ReplacementEvent>();
        _replacements.WhenForAnyArgs(r => r.AddEvent(default!)).Do(call => events.Add(call.Arg<ReplacementEvent>()));
        return events;
    }

    /// <summary>A refused request: the return state and status as arranged, nothing written.</summary>
    private void ShouldBeUnchanged(Replacement replacement)
    {
        (replacement.Status, replacement.ReturnState).ShouldBe((ReplacementStatus.AwaitingReturn, ReturnState.Mismatch));
        replacement.Events.ShouldAllBe(e => e.RecordedAt < Now, "an applied event would be recorded at the current time");
        _replacements.DidNotReceiveWithAnyArgs().AddEvent(default!);
        _trail.ReceivedCalls().ShouldBeEmpty();
        _notifications.ReceivedCalls().ShouldBeEmpty();
    }
}
