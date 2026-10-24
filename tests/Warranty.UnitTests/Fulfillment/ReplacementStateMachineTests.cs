using Warranty.Domain.Fulfillment;

namespace Warranty.UnitTests.Fulfillment;

/// <summary>
/// The replacement state machine of data-model.md (003 T318; FR-012 – FR-016, FR-021, FR-024): every pair of current
/// status × staff event × staff role is either allowed (with the listed target status and one appended event), refused for
/// the role (an <see cref="UnauthorizedAccessException"/>: auditors record nothing, claims agents no reviewer-only event),
/// or refused for the status (an <see cref="InvalidOperationException"/> naming the status, nothing changed);
/// <see cref="Replacement.AllowedEvents"/> lists exactly the allowed events per status and role; the system cancellation
/// is allowed from every status except the final ones; <see cref="ReplacementStatus.HandedToCustomer"/> is final.
/// <para>
/// Skips name every task a case still waits for, beyond the US1 transitions it implies: US1 events (LocationChanged,
/// UnitReserved, HandedToCustomer, ReturnRecorded under AtCollection) and the whole of <see cref="Replacement.AllowedEvents"/>
/// in T325; US2 (ReturnRecorded under BeforeReservation, the mismatch, ReturnAccepted) in T336; US3 (ModelProposed,
/// ModelApproved, ModelDeclined, NotAvailable) in T343; the system cancellation in T345; US5 (UnitRefused, Moved,
/// Correction) in T355. A case pending two stories ("Pending T336 and T343") is unskipped by the later of the two.
/// Corrections are covered by <see cref="Replacement.AllowedEvents"/> and the final statuses here and in detail by
/// ReplacementMoveCorrectionTests (T353).
/// </para>
/// </summary>
public sealed class ReplacementStateMachineTests
{
    private const string AgentActor = "agent-sub-t318";
    private const string ReviewerActor = "reviewer-sub-t318";
    private const string AuditorActor = "auditor-sub-t318";
    private const string Reason = "The customer asked for a location closer to home.";
    private const string ClaimSerial = "SN-AUR-PHN-0001";
    private const string WrongSerial = "SN-AUR-PHN-9999";
    private const string UnitSerial = "SN-AUR-PHN-7001";

    private const string T325 = "T325";
    private const string T336 = "T336";
    private const string T343 = "T343";
    private const string T345 = "T345";
    private const string T355 = "T355";

    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid ClaimId = Guid.Parse("0199c000-0000-7000-8000-000000000318");
    private static readonly Guid PolicyVersion = Guid.Parse("22222222-2222-7222-8222-222222222221");
    private static readonly Guid SuggestedLocation = Guid.Parse("33333333-3333-7333-8333-333333333331");
    private static readonly Guid OtherLocation = Guid.Parse("33333333-3333-7333-8333-333333333332");
    private static readonly Guid ClaimedProduct = Guid.Parse("44444444-4444-7444-8444-444444444441");
    private static readonly Guid EquivalentProduct = Guid.Parse("44444444-4444-7444-8444-444444444442");
    private static readonly Guid RemedyDecision = Guid.Parse("55555555-5555-7555-8555-555555555551");
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly FirstDay = new(2026, 10, 2);

    /// <summary>
    /// The starting points of the table. <see cref="ReplacementStatus.AwaitingUnit"/> and
    /// <see cref="ReplacementStatus.ReadyForCollection"/> are split by the return state and the pending proposal the
    /// table's rules depend on; the <c>AwaitingReturn…</c> and <see cref="AwaitingUnitReturned"/> stages are under
    /// <see cref="ReplacementReturnRule.BeforeReservation"/>, every other stage under <see cref="ReplacementReturnRule.AtCollection"/>.
    /// Every stage has the <see cref="ReplacementProductRule.SameCategoryAtLeastClaimValue"/> rule and the suggested location.
    /// </summary>
    public enum Stage
    {
        /// <summary>BeforeReservation, nothing returned.</summary>
        AwaitingReturn,

        /// <summary>BeforeReservation, a returned product whose serial did not match.</summary>
        AwaitingReturnMismatch,

        /// <summary>AtCollection, nothing returned, no proposal.</summary>
        AwaitingUnit,

        /// <summary>BeforeReservation, the matching product returned.</summary>
        AwaitingUnitReturned,

        /// <summary>AtCollection, a model proposal pending.</summary>
        AwaitingUnitWithProposal,

        /// <summary>AtCollection, a returned product whose serial did not match.</summary>
        AwaitingUnitMismatch,

        /// <summary>AtCollection, a unit reserved, nothing returned.</summary>
        ReadyForCollection,

        /// <summary>AtCollection, a unit reserved, a returned product whose serial did not match.</summary>
        ReadyForCollectionMismatch,

        HandedToCustomer,
        NotAvailable,
        Cancelled,
    }

    /// <summary>The events a staff member can record (ReplacementEventRequest.type) except Correction.</summary>
    private static readonly ReplacementEventType[] AppliedEvents =
    [
        ReplacementEventType.LocationChanged,
        ReplacementEventType.ReturnRecorded,
        ReplacementEventType.ReturnAccepted,
        ReplacementEventType.ModelProposed,
        ReplacementEventType.ModelApproved,
        ReplacementEventType.ModelDeclined,
        ReplacementEventType.UnitReserved,
        ReplacementEventType.UnitRefused,
        ReplacementEventType.Moved,
        ReplacementEventType.HandedToCustomer,
        ReplacementEventType.NotAvailable,
    ];

    /// <summary>Events only a claims reviewer may record (research R5, FR-024).</summary>
    private static readonly ReplacementEventType[] ReviewerOnlyEvents =
        [ReplacementEventType.ReturnAccepted, ReplacementEventType.ModelApproved, ReplacementEventType.ModelDeclined];

    private static readonly ReplacementEventType[] Us1Events =
    [
        ReplacementEventType.LocationChanged, ReplacementEventType.ReturnRecorded, ReplacementEventType.UnitReserved,
        ReplacementEventType.HandedToCustomer,
    ];

    /// <summary>The allowed transitions of data-model.md for a claims reviewer: (from, event) → to. Every other pair is refused.</summary>
    private static readonly Dictionary<(Stage From, ReplacementEventType Event), ReplacementStatus> Allowed = new()
    {
        [(Stage.AwaitingReturn, ReplacementEventType.LocationChanged)] = ReplacementStatus.AwaitingReturn,
        [(Stage.AwaitingReturn, ReplacementEventType.ReturnRecorded)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingReturn, ReplacementEventType.Moved)] = ReplacementStatus.AwaitingReturn,

        [(Stage.AwaitingReturnMismatch, ReplacementEventType.LocationChanged)] = ReplacementStatus.AwaitingReturn,
        [(Stage.AwaitingReturnMismatch, ReplacementEventType.ReturnAccepted)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingReturnMismatch, ReplacementEventType.Moved)] = ReplacementStatus.AwaitingReturn,

        [(Stage.AwaitingUnit, ReplacementEventType.LocationChanged)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingUnit, ReplacementEventType.ReturnRecorded)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingUnit, ReplacementEventType.ModelProposed)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingUnit, ReplacementEventType.UnitReserved)] = ReplacementStatus.ReadyForCollection,
        [(Stage.AwaitingUnit, ReplacementEventType.Moved)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingUnit, ReplacementEventType.NotAvailable)] = ReplacementStatus.NotAvailable,

        [(Stage.AwaitingUnitReturned, ReplacementEventType.LocationChanged)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingUnitReturned, ReplacementEventType.ModelProposed)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingUnitReturned, ReplacementEventType.UnitReserved)] = ReplacementStatus.ReadyForCollection,
        [(Stage.AwaitingUnitReturned, ReplacementEventType.Moved)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingUnitReturned, ReplacementEventType.NotAvailable)] = ReplacementStatus.NotAvailable,

        [(Stage.AwaitingUnitWithProposal, ReplacementEventType.LocationChanged)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingUnitWithProposal, ReplacementEventType.ReturnRecorded)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingUnitWithProposal, ReplacementEventType.ModelApproved)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingUnitWithProposal, ReplacementEventType.ModelDeclined)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingUnitWithProposal, ReplacementEventType.UnitReserved)] = ReplacementStatus.ReadyForCollection,
        [(Stage.AwaitingUnitWithProposal, ReplacementEventType.Moved)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingUnitWithProposal, ReplacementEventType.NotAvailable)] = ReplacementStatus.NotAvailable,

        [(Stage.AwaitingUnitMismatch, ReplacementEventType.LocationChanged)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingUnitMismatch, ReplacementEventType.ReturnAccepted)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingUnitMismatch, ReplacementEventType.ModelProposed)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingUnitMismatch, ReplacementEventType.Moved)] = ReplacementStatus.AwaitingUnit,
        [(Stage.AwaitingUnitMismatch, ReplacementEventType.NotAvailable)] = ReplacementStatus.NotAvailable,

        [(Stage.ReadyForCollection, ReplacementEventType.ReturnRecorded)] = ReplacementStatus.ReadyForCollection,
        [(Stage.ReadyForCollection, ReplacementEventType.UnitRefused)] = ReplacementStatus.AwaitingUnit,
        [(Stage.ReadyForCollection, ReplacementEventType.Moved)] = ReplacementStatus.AwaitingUnit,
        [(Stage.ReadyForCollection, ReplacementEventType.HandedToCustomer)] = ReplacementStatus.HandedToCustomer,

        [(Stage.ReadyForCollectionMismatch, ReplacementEventType.ReturnAccepted)] = ReplacementStatus.ReadyForCollection,
        [(Stage.ReadyForCollectionMismatch, ReplacementEventType.UnitRefused)] = ReplacementStatus.AwaitingUnit,
        [(Stage.ReadyForCollectionMismatch, ReplacementEventType.Moved)] = ReplacementStatus.AwaitingUnit,
    };

    /// <summary>The full list per status of the table for a claims reviewer (all stories), including Correction.</summary>
    private static readonly Dictionary<Stage, ReplacementEventType[]> ReviewerAllowedEvents = new()
    {
        [Stage.AwaitingReturn] =
        [
            ReplacementEventType.LocationChanged, ReplacementEventType.ReturnRecorded, ReplacementEventType.Moved,
            ReplacementEventType.Correction,
        ],
        [Stage.AwaitingReturnMismatch] =
        [
            ReplacementEventType.LocationChanged, ReplacementEventType.ReturnAccepted, ReplacementEventType.Moved,
            ReplacementEventType.Correction,
        ],
        [Stage.AwaitingUnit] =
        [
            ReplacementEventType.LocationChanged, ReplacementEventType.ReturnRecorded, ReplacementEventType.ModelProposed,
            ReplacementEventType.UnitReserved, ReplacementEventType.Moved, ReplacementEventType.NotAvailable,
            ReplacementEventType.Correction,
        ],
        [Stage.AwaitingUnitReturned] =
        [
            ReplacementEventType.LocationChanged, ReplacementEventType.ModelProposed, ReplacementEventType.UnitReserved,
            ReplacementEventType.Moved, ReplacementEventType.NotAvailable, ReplacementEventType.Correction,
        ],
        [Stage.AwaitingUnitWithProposal] =
        [
            ReplacementEventType.LocationChanged, ReplacementEventType.ReturnRecorded, ReplacementEventType.ModelApproved,
            ReplacementEventType.ModelDeclined, ReplacementEventType.UnitReserved, ReplacementEventType.Moved,
            ReplacementEventType.NotAvailable, ReplacementEventType.Correction,
        ],
        [Stage.AwaitingUnitMismatch] =
        [
            ReplacementEventType.LocationChanged, ReplacementEventType.ReturnAccepted, ReplacementEventType.ModelProposed,
            ReplacementEventType.Moved, ReplacementEventType.NotAvailable, ReplacementEventType.Correction,
        ],
        [Stage.ReadyForCollection] =
        [
            ReplacementEventType.ReturnRecorded, ReplacementEventType.UnitRefused, ReplacementEventType.Moved,
            ReplacementEventType.HandedToCustomer, ReplacementEventType.Correction,
        ],
        [Stage.ReadyForCollectionMismatch] =
        [
            ReplacementEventType.ReturnAccepted, ReplacementEventType.UnitRefused, ReplacementEventType.Moved,
            ReplacementEventType.Correction,
        ],
        [Stage.HandedToCustomer] = [],
        [Stage.NotAvailable] = [ReplacementEventType.Correction],
        [Stage.Cancelled] = [],
    };

    /// <summary>Every status × event × role pair.</summary>
    public static IEnumerable<TheoryDataRow<Stage, ReplacementEventType, StaffRole>> Pairs =>
        from stage in Enum.GetValues<Stage>()
        from type in AppliedEvents
        from role in Enum.GetValues<StaffRole>()
        select new TheoryDataRow<Stage, ReplacementEventType, StaffRole>(stage, type, role)
        {
            Skip = Pending([.. SetupTasks(stage), EventTask(stage, type)]),
        };

    /// <summary>Every status (test data only, nothing pending).</summary>
    public static TheoryData<Stage> Stages => [.. Enum.GetValues<Stage>()];

    /// <summary>Every status × role.</summary>
    public static IEnumerable<TheoryDataRow<Stage, StaffRole>> StagesAndRoles =>
        from stage in Enum.GetValues<Stage>()
        from role in Enum.GetValues<StaffRole>()
        select new TheoryDataRow<Stage, StaffRole>(stage, role) { Skip = Pending([.. SetupTasks(stage), T325]) };

    /// <summary>Every status, for the system cancellation.</summary>
    public static IEnumerable<TheoryDataRow<Stage>> CancellationStages =>
        from stage in Enum.GetValues<Stage>()
        select new TheoryDataRow<Stage>(stage) { Skip = Pending([.. SetupTasks(stage), T345]) };

    /// <summary>Every event (staff events, Correction and the system cancellation) after the hand-over to the customer.</summary>
    public static IEnumerable<TheoryDataRow<ReplacementEventType>> EventsAfterHandOver =>
        from type in AppliedEvents.Append(ReplacementEventType.Correction).Append(ReplacementEventType.Cancelled)
        select new TheoryDataRow<ReplacementEventType>(type)
        {
            Skip = Pending([.. SetupTasks(Stage.HandedToCustomer), EventTask(Stage.HandedToCustomer, type)]),
        };

    /// <summary>Every event (staff events, Correction and the system cancellation) after a cancellation.</summary>
    public static IEnumerable<TheoryDataRow<ReplacementEventType>> EventsAfterCancellation =>
        from type in AppliedEvents.Append(ReplacementEventType.Correction).Append(ReplacementEventType.Cancelled)
        select new TheoryDataRow<ReplacementEventType>(type)
        {
            Skip = Pending([.. SetupTasks(Stage.Cancelled), EventTask(Stage.Cancelled, type)]),
        };

    // ── The transition table and its role rules ────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Every_status_event_role_triple_is_allowed_or_refused_as_listed(Stage from, ReplacementEventType type, StaffRole role)
    {
        var replacement = ReplacementAt(from);
        var status = replacement.Status;
        var eventCount = replacement.Events.Count;

        if (role == StaffRole.Auditor || (role == StaffRole.ClaimsAgent && ReviewerOnlyEvents.Contains(type)))
        {
            Should.Throw<UnauthorizedAccessException>(() => Apply(replacement, type, role));

            replacement.Status.ShouldBe(status);
            replacement.Events.Count.ShouldBe(eventCount);
        }
        else if (Allowed.TryGetValue((from, type), out var to))
        {
            var recorded = Apply(replacement, type, role);

            replacement.Status.ShouldBe(to);
            replacement.Events.Count.ShouldBe(eventCount + 1);
            replacement.Events[^1].ShouldBeSameAs(recorded);
            (recorded.Type, recorded.FromStatus, recorded.ToStatus, recorded.Seq).ShouldBe(
                (type, status, replacement.Status, eventCount + 1));
            (recorded.Actor, recorded.Source).ShouldBe((ActorOf(role), ReportSource.Phone));
        }
        else
        {
            var refused = Should.Throw<InvalidOperationException>(() => Apply(replacement, type, role));

            refused.Message.ShouldContain(status.ToString());
            replacement.Status.ShouldBe(status);
            replacement.Events.Count.ShouldBe(eventCount);
        }
    }

    [Theory]
    [MemberData(nameof(StagesAndRoles))]
    public void Allowed_events_list_every_event_of_the_table_for_each_status_and_role(Stage stage, StaffRole role)
    {
        ReplacementEventType[] expected = role switch
        {
            StaffRole.ClaimsReviewer => ReviewerAllowedEvents[stage],
            StaffRole.ClaimsAgent => [.. ReviewerAllowedEvents[stage].Except(ReviewerOnlyEvents)],
            _ => [],
        };

        ReplacementAt(stage).AllowedEvents(role).ShouldBe(expected, ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(Stages))]
    public void The_allowed_event_lists_match_the_transition_table(Stage stage)
    {
        foreach (var type in AppliedEvents)
        {
            Allowed.ContainsKey((stage, type)).ShouldBe(ReviewerAllowedEvents[stage].Contains(type), $"{stage} × {type}");
        }

        ReviewerAllowedEvents[stage].Contains(ReplacementEventType.Correction)
            .ShouldBe(stage is not (Stage.HandedToCustomer or Stage.Cancelled), $"{stage} × Correction");
        ReviewerAllowedEvents[stage].ShouldNotContain(ReplacementEventType.Cancelled);
    }

    // ── The system cancellation (research R13) ─────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(CancellationStages))]
    public void A_remedy_change_cancels_the_replacement_from_every_status_except_the_hand_over_and_a_cancellation(Stage from)
    {
        var replacement = ReplacementAt(from);
        var status = replacement.Status;
        var eventCount = replacement.Events.Count;
        var cancelledAt = Report(9, StaffRole.ClaimsReviewer).RecordedAt;

        if (from is Stage.HandedToCustomer or Stage.Cancelled)
        {
            var refused = Should.Throw<InvalidOperationException>(() => replacement.Cancel(RemedyDecision, cancelledAt));

            refused.Message.ShouldContain(status.ToString());
            replacement.Status.ShouldBe(status);
            replacement.Events.Count.ShouldBe(eventCount);
            return;
        }

        var recorded = replacement.Cancel(RemedyDecision, cancelledAt);

        replacement.Status.ShouldBe(ReplacementStatus.Cancelled);
        replacement.Events.Count.ShouldBe(eventCount + 1);
        (recorded.Type, recorded.FromStatus, recorded.ToStatus, recorded.Seq).ShouldBe(
            (ReplacementEventType.Cancelled, status, ReplacementStatus.Cancelled, eventCount + 1));
        (recorded.Actor, recorded.Source).ShouldBe((ReplacementEvent.SystemActor, ReportSource.System));
        replacement.Units.ShouldAllBe(u => !u.IsReserved);
        replacement.AllowedEvents(StaffRole.ClaimsReviewer).ShouldBeEmpty();
    }

    // ── HandedToCustomer (and Cancelled) are final ─────────────────────────────────────────────

    [Fact(Skip = "Pending T325")]
    public void HandedToCustomer_is_final()
    {
        var replacement = ReplacementAt(Stage.HandedToCustomer);
        var eventCount = replacement.Events.Count;

        foreach (var role in Enum.GetValues<StaffRole>())
        {
            replacement.AllowedEvents(role).ShouldBeEmpty();
        }

        foreach (var type in Us1Events)
        {
            Should.Throw<InvalidOperationException>(() => Apply(replacement, type, StaffRole.ClaimsReviewer));
        }

        replacement.Status.ShouldBe(ReplacementStatus.HandedToCustomer);
        replacement.Events.Count.ShouldBe(eventCount);
        replacement.CurrentUnit.ShouldNotBeNull().Result.ShouldBe(ReplacementUnitResult.HandedOver);
    }

    [Theory]
    [MemberData(nameof(EventsAfterHandOver))]
    public void HandedToCustomer_accepts_no_further_event(ReplacementEventType type)
        => ShouldBeFinal(ReplacementAt(Stage.HandedToCustomer), type);

    [Theory]
    [MemberData(nameof(EventsAfterCancellation))]
    public void Cancelled_accepts_no_further_event(ReplacementEventType type)
        => ShouldBeFinal(ReplacementAt(Stage.Cancelled), type);

    // ── Rules of the US1 transitions ───────────────────────────────────────────────────────────

    [Fact(Skip = "Pending T325")]
    public void A_location_change_makes_the_chosen_location_the_only_active_one()
    {
        var replacement = ReplacementAt(Stage.AwaitingUnit);
        var suggested = replacement.ActiveLocation.ShouldNotBeNull();

        replacement.ChangeLocation(OtherLocation, Reason, Report(1, StaffRole.ClaimsAgent));

        var active = replacement.ActiveLocation.ShouldNotBeNull();
        (active.ServiceCenterId, active.Assignment, active.Reason, active.RecordedBy).ShouldBe(
            (OtherLocation, LocationAssignment.ChosenByStaff, Reason, AgentActor));
        suggested.IsActive.ShouldBeFalse();
        replacement.Locations.Count(l => l.IsActive).ShouldBe(1);
    }

    [Theory(Skip = "Pending T325")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_location_change_needs_a_reason(string? reason)
    {
        var replacement = ReplacementAt(Stage.AwaitingUnit);

        Should.Throw<ArgumentException>(() => replacement.ChangeLocation(OtherLocation, reason!, Report(1, StaffRole.ClaimsAgent)));

        replacement.ActiveLocation.ShouldNotBeNull().ServiceCenterId.ShouldBe(SuggestedLocation);
        replacement.Events.Count.ShouldBe(1);
    }

    [Fact(Skip = "Pending T325")]
    public void A_reservation_needs_an_active_location()
    {
        var replacement = Create(ReplacementReturnRule.AtCollection, ReplacementStatus.AwaitingUnit, suggestedLocation: null);

        replacement.AllowedEvents(StaffRole.ClaimsAgent).ShouldNotContain(ReplacementEventType.UnitReserved);
        replacement.AllowedEvents(StaffRole.ClaimsAgent).ShouldNotContain(ReplacementEventType.Moved);
        replacement.AllowedEvents(StaffRole.ClaimsAgent).ShouldContain(ReplacementEventType.LocationChanged);
        Should.Throw<InvalidOperationException>(
            () => replacement.ReserveUnit(ClaimedProduct, UnitSerial, Report(1, StaffRole.ClaimsAgent)));
        replacement.Status.ShouldBe(ReplacementStatus.AwaitingUnit);
        replacement.Units.ShouldBeEmpty();
        replacement.Events.ShouldBeEmpty();
    }

    [Fact(Skip = "Pending T325")]
    public void A_reservation_makes_the_unit_current_at_the_active_location_and_the_hand_over_ends_it_as_handed_over()
    {
        var replacement = ReplacementAt(Stage.AwaitingUnit);

        replacement.ReserveUnit(ClaimedProduct, " sn-aur-phn-7001 ", Report(1, StaffRole.ClaimsAgent));

        var unit = replacement.CurrentUnit.ShouldNotBeNull();
        (unit.Seq, unit.ServiceCenterId, unit.ProductId, unit.SerialNumber, unit.Result).ShouldBe(
            (1, SuggestedLocation, ClaimedProduct, UnitSerial, ReplacementUnitResult.Reserved));

        replacement.HandToCustomer(FirstDay.AddDays(2), returnConfirmed: true, Report(2, StaffRole.ClaimsAgent));

        unit.Result.ShouldBe(ReplacementUnitResult.HandedOver);
        replacement.CurrentUnit.ShouldBeSameAs(unit);
        replacement.ReturnState.ShouldBe(ReturnState.Returned);
        replacement.Events.Select(e => e.Seq).ShouldBe([1, 2, 3]);
    }

    [Fact(Skip = "Pending T325")]
    public void Under_AtCollection_the_hand_over_needs_the_return_confirmed_unless_it_was_recorded()
    {
        var replacement = ReplacementAt(Stage.ReadyForCollection);
        var eventCount = replacement.Events.Count;

        Should.Throw<ArgumentException>(
            () => replacement.HandToCustomer(FirstDay.AddDays(3), returnConfirmed: false, Report(3, StaffRole.ClaimsAgent)));
        replacement.Status.ShouldBe(ReplacementStatus.ReadyForCollection);
        replacement.Events.Count.ShouldBe(eventCount);

        replacement.RecordReturn(FirstDay.AddDays(3), ClaimSerial, serialMatches: true, Report(3, StaffRole.ClaimsAgent));
        replacement.HandToCustomer(FirstDay.AddDays(3), returnConfirmed: false, Report(3, StaffRole.ClaimsAgent));

        replacement.Status.ShouldBe(ReplacementStatus.HandedToCustomer);
        replacement.ReturnState.ShouldBe(ReturnState.Returned);
    }

    // ── Rules of the US3 transitions ───────────────────────────────────────────────────────────

    [Fact(Skip = "Pending T343")]
    public void Another_model_can_be_proposed_only_under_SameCategoryAtLeastClaimValue()
    {
        var replacement = Replacement.Create(
            Guid.CreateVersion7(), Tenant, ClaimId, PolicyVersion, ReplacementProductRule.SameModel, ReplacementReturnRule.AtCollection,
            minimumCoverMonths: 0, ReplacementStatus.AwaitingUnit, SuggestedLocation, CreatedAt);

        replacement.AllowedEvents(StaffRole.ClaimsAgent).ShouldNotContain(ReplacementEventType.ModelProposed);
        Should.Throw<InvalidOperationException>(
            () => replacement.ProposeModel(EquivalentProduct, Reason, Report(1, StaffRole.ClaimsAgent)));
        replacement.Proposals.ShouldBeEmpty();
        replacement.Events.Count.ShouldBe(1);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The skip of a case waiting for <paramref name="tasks"/> (null when nothing is pending). Every later story builds on
    /// US1 (T325), and the cancellation (T345) on the US3 transitions (T343), so those are named only on their own.
    /// </summary>
    private static string? Pending(string?[] tasks)
    {
        var pending = tasks.OfType<string>().Distinct().ToList();
        if (pending.Count > 1)
        {
            pending.Remove(T325);
        }

        if (pending.Contains(T345))
        {
            pending.Remove(T343);
        }

        return pending.Count == 0 ? null : "Pending " + string.Join(" and ", pending.Order(StringComparer.Ordinal));
    }

    /// <summary>The tasks whose transitions <see cref="ReplacementAt"/> uses to reach <paramref name="stage"/>.</summary>
    private static string?[] SetupTasks(Stage stage) => stage switch
    {
        Stage.AwaitingReturn or Stage.AwaitingUnit => [],
        Stage.AwaitingReturnMismatch or Stage.AwaitingUnitReturned or Stage.AwaitingUnitMismatch => [T336],
        Stage.ReadyForCollectionMismatch => [T325, T336],
        Stage.AwaitingUnitWithProposal or Stage.NotAvailable => [T343],
        Stage.ReadyForCollection or Stage.HandedToCustomer => [T325],
        Stage.Cancelled => [T345],
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
    };

    /// <summary>The task implementing <paramref name="type"/> (ReturnRecorded depends on the stage's return rule).</summary>
    private static string EventTask(Stage stage, ReplacementEventType type) => type switch
    {
        ReplacementEventType.LocationChanged or ReplacementEventType.UnitReserved or ReplacementEventType.HandedToCustomer => T325,
        ReplacementEventType.ReturnRecorded => ReturnRuleOf(stage) == ReplacementReturnRule.AtCollection ? T325 : T336,
        ReplacementEventType.ReturnAccepted => T336,
        ReplacementEventType.ModelProposed or ReplacementEventType.ModelApproved or ReplacementEventType.ModelDeclined
            or ReplacementEventType.NotAvailable => T343,
        ReplacementEventType.Cancelled => T345,
        ReplacementEventType.UnitRefused or ReplacementEventType.Moved or ReplacementEventType.Correction => T355,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    private static ReplacementReturnRule ReturnRuleOf(Stage stage)
        => stage is Stage.AwaitingReturn or Stage.AwaitingReturnMismatch or Stage.AwaitingUnitReturned
            ? ReplacementReturnRule.BeforeReservation
            : ReplacementReturnRule.AtCollection;

    private static string ActorOf(StaffRole role) => role switch
    {
        StaffRole.ClaimsAgent => AgentActor,
        StaffRole.ClaimsReviewer => ReviewerActor,
        _ => AuditorActor,
    };

    /// <summary>A report by phone with <paramref name="role"/>, <paramref name="day"/> days after the first day.</summary>
    private static ReplacementReport Report(int day, StaffRole role)
    {
        var at = new DateTimeOffset(FirstDay.AddDays(day).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        return new ReplacementReport(at, ReportSource.Phone, ActorOf(role), role, null, at.AddMinutes(5));
    }

    private static Replacement Create(ReplacementReturnRule returnRule, ReplacementStatus initialStatus, Guid? suggestedLocation)
        => Replacement.Create(
            Guid.CreateVersion7(), Tenant, ClaimId, PolicyVersion, ReplacementProductRule.SameCategoryAtLeastClaimValue, returnRule,
            minimumCoverMonths: 0, initialStatus, suggestedLocation, CreatedAt);

    /// <summary>A replacement with the suggested location, driven to <paramref name="stage"/> through the domain transitions.</summary>
    private static Replacement ReplacementAt(Stage stage)
    {
        var rule = ReturnRuleOf(stage);
        var initial = stage is Stage.AwaitingReturn or Stage.AwaitingReturnMismatch or Stage.AwaitingUnitReturned
            ? ReplacementStatus.AwaitingReturn
            : ReplacementStatus.AwaitingUnit;
        var replacement = Create(rule, initial, SuggestedLocation);
        var agent = StaffRole.ClaimsAgent;

        switch (stage)
        {
            case Stage.AwaitingReturnMismatch or Stage.AwaitingUnitMismatch:
                replacement.RecordReturn(FirstDay, WrongSerial, serialMatches: false, Report(0, agent));
                break;
            case Stage.AwaitingUnitReturned:
                replacement.RecordReturn(FirstDay, ClaimSerial, serialMatches: true, Report(0, agent));
                break;
            case Stage.AwaitingUnitWithProposal:
                replacement.ProposeModel(EquivalentProduct, Reason, Report(0, agent));
                break;
            case Stage.ReadyForCollection:
                replacement.ReserveUnit(ClaimedProduct, UnitSerial, Report(1, agent));
                break;
            case Stage.ReadyForCollectionMismatch:
                replacement.ReserveUnit(ClaimedProduct, UnitSerial, Report(1, agent));
                replacement.RecordReturn(FirstDay.AddDays(2), WrongSerial, serialMatches: false, Report(2, agent));
                break;
            case Stage.HandedToCustomer:
                replacement.ReserveUnit(ClaimedProduct, UnitSerial, Report(1, agent));
                replacement.HandToCustomer(FirstDay.AddDays(2), returnConfirmed: true, Report(2, agent));
                break;
            case Stage.NotAvailable:
                replacement.MarkNotAvailable("No unit of the model is left at any location.", Report(1, agent));
                break;
            case Stage.Cancelled:
                replacement.Cancel(RemedyDecision, Report(1, agent).RecordedAt);
                break;
        }

        return replacement;
    }

    /// <summary>Records <paramref name="type"/> with <paramref name="role"/> and valid fields for that event.</summary>
    private static ReplacementEvent Apply(Replacement replacement, ReplacementEventType type, StaffRole role)
    {
        var report = Report(5, role);
        return type switch
        {
            ReplacementEventType.LocationChanged => replacement.ChangeLocation(OtherLocation, Reason, report),
            ReplacementEventType.ReturnRecorded =>
                replacement.RecordReturn(FirstDay.AddDays(5), ClaimSerial, serialMatches: true, report),
            ReplacementEventType.ReturnAccepted =>
                replacement.AcceptReturn("The claim's serial had a typo; the returned product is the claimed one.", report),
            ReplacementEventType.ModelProposed => replacement.ProposeModel(EquivalentProduct, Reason, report),
            ReplacementEventType.ModelApproved => replacement.ApproveModel(report),
            ReplacementEventType.ModelDeclined => replacement.DeclineModel("The proposed model lacks the claimed features.", report),
            ReplacementEventType.UnitReserved => replacement.ReserveUnit(ClaimedProduct, "SN-AUR-PHN-7002", report),
            ReplacementEventType.UnitRefused => replacement.RefuseUnit("The customer refused the unit: scratched screen.", report),
            ReplacementEventType.Moved => replacement.Move(OtherLocation, Reason, report),
            ReplacementEventType.HandedToCustomer => replacement.HandToCustomer(FirstDay.AddDays(5), returnConfirmed: true, report),
            ReplacementEventType.NotAvailable => replacement.MarkNotAvailable("No unit of the model is left at any location.", report),
            ReplacementEventType.Correction => replacement.Correct(
                replacement.Events.Where(e => e.Actor != ReplacementEvent.SystemActor).Select(e => e.Seq).DefaultIfEmpty(1).Max(),
                "The date was entered wrongly.",
                new ReplacementCorrection(HandedOverOn: FirstDay.AddDays(3)),
                report),
            ReplacementEventType.Cancelled => replacement.Cancel(RemedyDecision, report.RecordedAt),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not an event of this test."),
        };
    }

    /// <summary>A final replacement refuses <paramref name="type"/> (as a reviewer) and stays unchanged.</summary>
    private static void ShouldBeFinal(Replacement replacement, ReplacementEventType type)
    {
        var status = replacement.Status;
        var eventCount = replacement.Events.Count;

        var refused = Should.Throw<InvalidOperationException>(() => Apply(replacement, type, StaffRole.ClaimsReviewer));

        refused.Message.ShouldContain(status.ToString());
        replacement.Status.ShouldBe(status);
        replacement.Events.Count.ShouldBe(eventCount);
        replacement.AllowedEvents(StaffRole.ClaimsReviewer).ShouldBeEmpty();
    }
}
