using Warranty.Domain.Fulfillment;

namespace Warranty.UnitTests.Fulfillment;

/// <summary>
/// The repair state machine of data-model.md (002 T234; FR-024): every pair of current status × staff event is
/// either allowed (with the listed target status and one appended event) or refused (an
/// <see cref="InvalidOperationException"/>, nothing changed), <see cref="Repair.AllowedEvents"/> lists exactly the
/// allowed events per status, and <see cref="RepairStatus.Collected"/> is final.
/// <para>
/// US2 events (CentreChanged, HandedOver, MarkedReady, Collected) are implemented in T238; US3 events
/// (EstimateReported, RepairFailed, NewAttemptStarted) and the <see cref="RepairStatus.RepairNotPossible"/> states in
/// T247; US5 events (Transferred, Correction) in T256. Corrections are covered by <see cref="Repair.AllowedEvents"/>
/// here and in detail by RepairTransferCorrectionTests (T254).
/// </para>
/// </summary>
public sealed class RepairStateMachineTests
{
    private const string PendingUs3 = "Pending T247";
    private const string PendingUs5 = "Pending T256";
    private const string PendingUs3AndUs5 = "Pending T247 and T256";

    private const string Actor = "agent-sub-t234";
    private const string Reason = "The customer asked for a centre closer to home.";
    private const decimal EconomicLimit = 288m;

    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid ClaimId = Guid.Parse("0199b000-0000-7000-8000-000000000234");
    private static readonly Guid SuggestedCentre = Guid.Parse("33333333-3333-7333-8333-333333333331");
    private static readonly Guid OtherCentre = Guid.Parse("33333333-3333-7333-8333-333333333332");
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly HandedOverOn = new(2026, 9, 2);

    /// <summary>The starting points of the table; <see cref="RepairStatus.RepairNotPossible"/> is split by its reason.</summary>
    public enum Stage
    {
        AwaitingHandover,
        AtRepairCentre,
        ReadyForCollection,
        Collected,
        NotPossibleFailed,
        NotPossibleNotEconomical,
    }

    /// <summary>The events a staff member can record (RepairEventRequest.type) except Correction.</summary>
    private static readonly RepairEventType[] AppliedEvents =
    [
        RepairEventType.CentreChanged,
        RepairEventType.HandedOver,
        RepairEventType.EstimateReported,
        RepairEventType.MarkedReady,
        RepairEventType.Collected,
        RepairEventType.RepairFailed,
        RepairEventType.NewAttemptStarted,
        RepairEventType.Transferred,
    ];

    private static readonly RepairEventType[] Us2Events =
        [RepairEventType.CentreChanged, RepairEventType.HandedOver, RepairEventType.MarkedReady, RepairEventType.Collected];

    private static readonly RepairEventType[] Us3Events =
        [RepairEventType.EstimateReported, RepairEventType.RepairFailed, RepairEventType.NewAttemptStarted];

    /// <summary>The allowed transitions of data-model.md: (from, event) → to. Every other pair is refused.</summary>
    private static readonly Dictionary<(Stage From, RepairEventType Event), RepairStatus> Allowed = new()
    {
        [(Stage.AwaitingHandover, RepairEventType.CentreChanged)] = RepairStatus.AwaitingHandover,
        [(Stage.AwaitingHandover, RepairEventType.HandedOver)] = RepairStatus.AtRepairCentre,
        [(Stage.AwaitingHandover, RepairEventType.Transferred)] = RepairStatus.AwaitingHandover,
        [(Stage.AtRepairCentre, RepairEventType.EstimateReported)] = RepairStatus.AtRepairCentre,
        [(Stage.AtRepairCentre, RepairEventType.MarkedReady)] = RepairStatus.ReadyForCollection,
        [(Stage.AtRepairCentre, RepairEventType.RepairFailed)] = RepairStatus.RepairNotPossible,
        [(Stage.AtRepairCentre, RepairEventType.Transferred)] = RepairStatus.AwaitingHandover,
        [(Stage.ReadyForCollection, RepairEventType.Collected)] = RepairStatus.Collected,
        [(Stage.NotPossibleFailed, RepairEventType.NewAttemptStarted)] = RepairStatus.AtRepairCentre,
    };

    /// <summary>Every status × event pair, skipped until the tasks it needs are merged.</summary>
    public static IEnumerable<TheoryDataRow<Stage, RepairEventType, bool>> Pairs =>
        from stage in Enum.GetValues<Stage>()
        from type in AppliedEvents
        select new TheoryDataRow<Stage, RepairEventType, bool>(stage, type, Allowed.ContainsKey((stage, type)))
        {
            Skip = SkipFor(stage, type),
        };

    /// <summary>The full list per status of the table (all stories), including Correction everywhere except Collected.</summary>
    public static TheoryData<Stage, RepairEventType[]> FullAllowedEvents => new()
    {
        {
            Stage.AwaitingHandover,
            [RepairEventType.CentreChanged, RepairEventType.HandedOver, RepairEventType.Transferred, RepairEventType.Correction]
        },
        {
            Stage.AtRepairCentre,
            [RepairEventType.EstimateReported, RepairEventType.MarkedReady, RepairEventType.RepairFailed, RepairEventType.Transferred,
                RepairEventType.Correction]
        },
        { Stage.ReadyForCollection, [RepairEventType.Collected, RepairEventType.Correction] },
        { Stage.Collected, [] },
        { Stage.NotPossibleFailed, [RepairEventType.NewAttemptStarted, RepairEventType.Correction] },
        { Stage.NotPossibleNotEconomical, [RepairEventType.Correction] },
    };

    // ── The transition table ───────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Every_status_event_pair_is_allowed_or_refused_as_listed(Stage from, RepairEventType type, bool allowed)
    {
        var repair = RepairAt(from);
        var status = repair.Status;
        var eventCount = repair.Events.Count;

        if (allowed)
        {
            var recorded = Apply(repair, type);

            repair.Status.ShouldBe(Allowed[(from, type)]);
            repair.Events.Count.ShouldBe(eventCount + 1);
            repair.Events[^1].ShouldBeSameAs(recorded);
            (recorded.Type, recorded.FromStatus, recorded.ToStatus, recorded.Seq).ShouldBe(
                (type, status, repair.Status, eventCount + 1));
            (recorded.Actor, recorded.Source).ShouldBe((Actor, ReportSource.Phone));
        }
        else
        {
            var refused = Should.Throw<InvalidOperationException>(() => Apply(repair, type));

            refused.Message.ShouldContain(status.ToString());
            repair.Status.ShouldBe(status);
            repair.Events.Count.ShouldBe(eventCount);
        }
    }

    [Theory]
    [InlineData(Stage.AwaitingHandover, new[] { RepairEventType.CentreChanged, RepairEventType.HandedOver })]
    [InlineData(Stage.AtRepairCentre, new[] { RepairEventType.MarkedReady })]
    [InlineData(Stage.ReadyForCollection, new[] { RepairEventType.Collected })]
    [InlineData(Stage.Collected, new RepairEventType[0])]
    public void Allowed_events_list_the_US2_events_of_each_status(Stage stage, RepairEventType[] expected)
    {
        var repair = RepairAt(stage);

        repair.AllowedEvents().Where(Us2Events.Contains).ShouldBe(expected, ignoreOrder: true);
    }

    [Theory(Skip = PendingUs3AndUs5)]
    [MemberData(nameof(FullAllowedEvents))]
    public void Allowed_events_list_every_event_of_the_table_for_each_status(Stage stage, RepairEventType[] expected)
    {
        var repair = RepairAt(stage);

        repair.AllowedEvents().ShouldBe(expected, ignoreOrder: true);
        foreach (var type in AppliedEvents)
        {
            Allowed.ContainsKey((stage, type)).ShouldBe(expected.Contains(type), $"{stage} × {type}");
        }
    }

    // ── Collected is final ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Collected_is_final()
    {
        var repair = RepairAt(Stage.Collected);
        var eventCount = repair.Events.Count;

        repair.AllowedEvents().ShouldBeEmpty();
        foreach (var type in Us2Events)
        {
            Should.Throw<InvalidOperationException>(() => Apply(repair, type));
        }

        repair.Status.ShouldBe(RepairStatus.Collected);
        repair.Events.Count.ShouldBe(eventCount);
        repair.CurrentAttempt.ShouldNotBeNull().Result.ShouldBe(AttemptResult.Collected);
    }

    // ── Rules of the US2 transitions ───────────────────────────────────────────────────────────

    [Fact]
    public void The_first_hand_over_opens_attempt_1_at_the_active_centre_and_records_the_date_and_reference()
    {
        var repair = RepairAt(Stage.AwaitingHandover);

        repair.RecordHandover(HandedOverOn, "JOB-1001", Report(1));

        var attempt = repair.CurrentAttempt.ShouldNotBeNull();
        (attempt.Seq, attempt.ServiceCenterId, attempt.Result).ShouldBe((1, SuggestedCentre, AttemptResult.Open));
        repair.Attempts.ShouldHaveSingleItem();
        var handover = repair.ActiveHandover.ShouldNotBeNull();
        (handover.HandedOverOn, handover.CentreReference).ShouldBe(((DateOnly?)HandedOverOn, "JOB-1001"));
    }

    [Fact]
    public void A_hand_over_needs_an_active_centre()
    {
        var repair = Repair.Create(Guid.CreateVersion7(), Tenant, ClaimId, suggestedServiceCenterId: null, CreatedAt);

        repair.AllowedEvents().ShouldNotContain(RepairEventType.HandedOver);
        repair.AllowedEvents().ShouldContain(RepairEventType.CentreChanged);
        Should.Throw<InvalidOperationException>(() => repair.RecordHandover(HandedOverOn, null, Report(1)));
        repair.Status.ShouldBe(RepairStatus.AwaitingHandover);
        repair.Attempts.ShouldBeEmpty();
        repair.Events.ShouldBeEmpty();
    }

    [Fact]
    public void A_centre_change_makes_the_chosen_centre_the_only_active_hand_over()
    {
        var repair = RepairAt(Stage.AwaitingHandover);
        var suggested = repair.ActiveHandover.ShouldNotBeNull();

        repair.ChangeCentre(OtherCentre, Reason, Report(1));

        var active = repair.ActiveHandover.ShouldNotBeNull();
        (active.ServiceCenterId, active.Assignment, active.Reason, active.RecordedBy).ShouldBe(
            (OtherCentre, HandoverAssignment.ChosenByStaff, Reason, Actor));
        suggested.IsActive.ShouldBeFalse();
        repair.Handovers.Count(h => h.IsActive).ShouldBe(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_centre_change_needs_a_reason(string? reason)
    {
        var repair = RepairAt(Stage.AwaitingHandover);

        Should.Throw<ArgumentException>(() => repair.ChangeCentre(OtherCentre, reason!, Report(1)));

        repair.ActiveHandover.ShouldNotBeNull().ServiceCenterId.ShouldBe(SuggestedCentre);
        repair.Events.Count.ShouldBe(1);
    }

    [Fact]
    public void Marking_ready_ends_the_attempt_as_ready_and_collection_as_collected()
    {
        var repair = RepairAt(Stage.AtRepairCentre);
        var attempt = repair.CurrentAttempt.ShouldNotBeNull();

        repair.MarkReady(Report(2));
        attempt.Result.ShouldBe(AttemptResult.Ready);

        repair.RecordCollection(Report(3));
        attempt.Result.ShouldBe(AttemptResult.Collected);
        repair.CurrentAttempt.ShouldBeSameAs(attempt);
        repair.Events.Select(e => e.Seq).ShouldBe([1, 2, 3, 4]);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static string? SkipFor(Stage stage, RepairEventType type)
    {
        var needsUs3 = Us3Events.Contains(type) || stage is Stage.NotPossibleFailed or Stage.NotPossibleNotEconomical;
        var needsUs5 = type is RepairEventType.Transferred or RepairEventType.Correction;
        return (needsUs3, needsUs5) switch
        {
            (true, true) => PendingUs3AndUs5,
            (true, false) => PendingUs3,
            (false, true) => PendingUs5,
            _ => null,
        };
    }

    /// <summary>A report by <see cref="Actor"/> by phone, <paramref name="day"/> days after the hand-over.</summary>
    private static RepairReport Report(int day)
    {
        var at = new DateTimeOffset(HandedOverOn.AddDays(day).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        return new RepairReport(at, ReportSource.Phone, Actor, null, at.AddMinutes(5));
    }

    /// <summary>A repair with a suggested centre, driven to <paramref name="stage"/> through the domain transitions.</summary>
    private static Repair RepairAt(Stage stage)
    {
        var repair = Repair.Create(Guid.CreateVersion7(), Tenant, ClaimId, SuggestedCentre, CreatedAt);
        if (stage == Stage.AwaitingHandover)
        {
            return repair;
        }

        repair.RecordHandover(HandedOverOn, "JOB-1001", Report(0));
        switch (stage)
        {
            case Stage.ReadyForCollection:
                repair.MarkReady(Report(1));
                break;
            case Stage.Collected:
                repair.MarkReady(Report(1));
                repair.RecordCollection(Report(2));
                break;
            case Stage.NotPossibleFailed:
                repair.RecordFailure("The replacement board is not available.", Report(1));
                break;
            case Stage.NotPossibleNotEconomical:
                repair.RecordEstimate(EconomicLimit + 0.01m, EconomicLimit, Report(1));
                break;
        }

        return repair;
    }

    /// <summary>Records <paramref name="type"/> with valid fields for that event.</summary>
    private static RepairEvent Apply(Repair repair, RepairEventType type) => type switch
    {
        RepairEventType.CentreChanged => repair.ChangeCentre(OtherCentre, Reason, Report(5)),
        RepairEventType.HandedOver => repair.RecordHandover(HandedOverOn, "JOB-1002", Report(5)),
        RepairEventType.EstimateReported => repair.RecordEstimate(EconomicLimit, EconomicLimit, Report(5)),
        RepairEventType.MarkedReady => repair.MarkReady(Report(5)),
        RepairEventType.Collected => repair.RecordCollection(Report(5)),
        RepairEventType.RepairFailed => repair.RecordFailure("The fault came back after the board was replaced.", Report(5)),
        RepairEventType.NewAttemptStarted => repair.StartNewAttempt(ProductLocation.AtCentre, Report(5)),
        RepairEventType.Transferred => repair.Transfer(OtherCentre, Reason, Report(5)),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a staff event of this test."),
    };
}
