using System.Text.Json;
using Warranty.Application.Fulfillment;
using Warranty.Domain.Catalog;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Integration;

namespace Warranty.UnitTests.Fulfillment;

/// <summary>
/// The claimant's view of a Replacement remedy (003 T320; FR-032 – FR-034, data-model.md "Claimant projection"):
/// <see cref="ClaimantRemedyProjection"/> gives the label of each replacement status, the replacement timeline steps
/// (<c>Product returned</c> only under <see cref="ReplacementReturnRule.BeforeReservation"/> or for a product held by
/// a repair centre · <c>Ready for collection</c> · <c>Handed to you</c>), the active location's name, address and
/// opening hours and the reserved unit's model name, and never serial numbers, notes, return check results, reasons,
/// contact details or staff names. The projection reads the replacement through <see cref="ClaimantReplacementFacts"/>,
/// which carries no serial, note, reason or staff name, so a return check result can only leak through the view
/// differing between return states; the privacy cases check that it does not.
/// </summary>
public sealed class ClaimantReplacementProjectionTests
{
    private const string Pending = "Pending T330";
    private const string LocationContact = "replacement-desk@springfield-location.test";
    private const string OtherContact = "555-0303";

    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid ClaimId = Guid.Parse("0199c000-0000-7000-8000-000000000320");
    private static readonly Guid VersionId = Guid.Parse("0199c000-0000-7000-8000-0000000003b2");
    private static readonly DateTimeOffset ApprovedAt = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    private static readonly ServiceCenter Location = ServiceCenter.Create(
        Guid.Parse("33333333-3333-7333-8333-333333333341"), Tenant, Region.NA, "Aurora Service Center Springfield", ["tablet"],
        "1 Main Street, Springfield", "Mon-Fri 09:00-17:00", LocationContact, priority: 10, issuesReplacements: true);

    private static readonly ServiceCenter RepairCentre = ServiceCenter.Create(
        Guid.Parse("33333333-3333-7333-8333-333333333342"), Tenant, Region.NA, "Aurora Service Center Shelbyville", ["tablet"],
        "2 Oak Avenue, Shelbyville", "Mon-Sat 09:00-18:00", OtherContact, priority: 20, issuesReplacements: true);

    private static readonly Product UnitModel = Product.Create(
        Guid.Parse("44444444-4444-7444-8444-444444444411"), Tenant, "AUR-TAB11", "Aurora Tab 11", "tablet", 520m, "USD");

    /// <summary>
    /// The rows of data-model.md "Claimant projection": state → (remedy shown, label, completed). The
    /// <c>awaitingUnit…</c> rows cover a pending return check (mismatch), an accepted one and a held product.
    /// </summary>
    public static IEnumerable<TheoryDataRow<string, RemedyType?, string, bool>> Labels =>
    [
        Row("awaitingReturn", RemedyType.Replacement, "Please bring your product to the location below", false),
        Row("awaitingUnit", RemedyType.Replacement, "We are preparing your replacement", false),
        Row("awaitingUnitAfterReturn", RemedyType.Replacement, "We are preparing your replacement", false),
        Row("awaitingUnitWithReturnMismatch", RemedyType.Replacement, "We are preparing your replacement", false),
        Row("awaitingUnitAfterAcceptedReturn", RemedyType.Replacement, "We are preparing your replacement", false),
        Row("awaitingUnitHeldByRepairCentre", RemedyType.Replacement, "We are preparing your replacement", false),
        Row("readyForCollection", RemedyType.Replacement, "Your replacement is ready for collection", false),
        Row("handedToCustomer", RemedyType.Replacement, "Replacement handed over", true),
        Row("notAvailable", null, "Replacement not possible. Your claim is being reviewed.", false),
        Row("cancelledForRefund", RemedyType.Refund, "Refund being processed", false),
        Row("cancelledForRepair", RemedyType.Repair, "Take your product to the repair centre", false),
    ];

    /// <summary>
    /// Replacement timeline per state: the step of the current status is <c>current</c>, earlier ones <c>done</c>
    /// (as 002's Repair timeline); <c>Product returned</c> appears only under <c>BeforeReservation</c> or when the
    /// product is held by a repair centre.
    /// </summary>
    public static IEnumerable<TheoryDataRow<string, string[], string[]>> ReplacementSteps =>
    [
        Steps("awaitingReturn", ["Product returned", "Ready for collection", "Handed to you"], ["current", "upcoming", "upcoming"]),
        Steps("awaitingReturnWithMismatch", ["Product returned", "Ready for collection", "Handed to you"], ["current", "upcoming", "upcoming"]),
        Steps("awaitingUnitAfterReturn", ["Product returned", "Ready for collection", "Handed to you"], ["done", "current", "upcoming"]),
        Steps("readyForCollectionAfterReturn", ["Product returned", "Ready for collection", "Handed to you"], ["done", "done", "current"]),
        Steps("handedToCustomerAfterReturn", ["Product returned", "Ready for collection", "Handed to you"], ["done", "done", "done"]),
        Steps("awaitingUnit", ["Ready for collection", "Handed to you"], ["current", "upcoming"]),
        Steps("awaitingUnitWithReturnMismatch", ["Ready for collection", "Handed to you"], ["current", "upcoming"]),
        Steps("readyForCollection", ["Ready for collection", "Handed to you"], ["current", "upcoming"]),
        Steps("readyForCollectionReturnedEarly", ["Ready for collection", "Handed to you"], ["current", "upcoming"]),
        Steps("handedToCustomer", ["Ready for collection", "Handed to you"], ["done", "done"]),
        Steps("awaitingUnitHeldByRepairCentre", ["Product returned", "Ready for collection", "Handed to you"], ["done", "current", "upcoming"]),
        Steps("readyForCollectionHeldByRepairCentre", ["Product returned", "Ready for collection", "Handed to you"], ["done", "done", "current"]),
        Steps("handedToCustomerHeldByRepairCentre", ["Product returned", "Ready for collection", "Handed to you"], ["done", "done", "done"]),
    ];

    // ── Labels ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Labels))]
    public void Each_replacement_state_has_its_claimant_label(string state, RemedyType? remedy, string label, bool completed)
    {
        var (claimRemedy, repair, replacement) = Arrange(state);

        var view = ClaimantRemedyProjection.Project(claimRemedy, repair, repair is null ? null : RepairCentre, replacement)
            .ShouldNotBeNull();

        (view.Remedy, view.Label, view.Completed).ShouldBe((remedy, label, completed));
    }

    [Fact(Skip = Pending)]
    public void A_claim_without_a_remedy_has_no_remedy_view_even_with_a_replacement()
        => ClaimantRemedyProjection.Project(null, null, null, Facts(ReplacementStatus.AwaitingUnit)).ShouldBeNull();

    [Fact(Skip = Pending)]
    public void A_repair_or_refund_without_a_replacement_keeps_its_002_view()
    {
        var (remedy, repair, _) = Arrange("cancelledForRepair");

        var view = ClaimantRemedyProjection.Project(remedy, repair, RepairCentre, replacement: null).ShouldNotBeNull();

        JsonSerializer.Serialize(view, JsonSerializerOptions.Web)
            .ShouldBe(JsonSerializer.Serialize(ClaimantRemedyProjection.Project(remedy, repair, RepairCentre), JsonSerializerOptions.Web));
        (view.Location, view.ReplacementModelName, view.ReplacementSteps).ShouldBe((null, null, null));
    }

    // ── Replacement timeline, location card and model name ─────────────────────────────────────

    [Theory]
    [MemberData(nameof(ReplacementSteps))]
    public void A_replacement_shows_its_timeline_steps(string state, string[] titles, string[] stepStates)
    {
        var (claimRemedy, repair, replacement) = Arrange(state);

        var view = ClaimantRemedyProjection.Project(claimRemedy, repair, null, replacement).ShouldNotBeNull();

        var steps = view.ReplacementSteps.ShouldNotBeNull();
        steps.Select(s => s.Title).ShouldBe(titles);
        steps.Select(s => s.State).ShouldBe(stepStates);
        view.Steps.ShouldBeEmpty("the four Repair steps are for Repair only");
        view.RepairCentre.ShouldBeNull();
    }

    [Theory]
    [InlineData("awaitingReturn", Skip = Pending)]
    [InlineData("awaitingUnit", Skip = Pending)]
    [InlineData("readyForCollection", Skip = Pending)]
    [InlineData("handedToCustomer", Skip = Pending)]
    public void A_replacement_shows_the_location_name_address_and_opening_hours(string state)
    {
        var (claimRemedy, repair, replacement) = Arrange(state);

        var view = ClaimantRemedyProjection.Project(claimRemedy, repair, null, replacement).ShouldNotBeNull();

        view.Location.ShouldBe(new ClaimantRepairCentreView(Location.Name, Location.Address, Location.OpeningHours));
    }

    [Fact(Skip = Pending)]
    public void A_replacement_without_an_assigned_location_has_no_location_card()
    {
        var view = ClaimantRemedyProjection.Project(ReplacementByPolicy(), null, null, Facts(ReplacementStatus.AwaitingUnit) with { ActiveLocation = null })
            .ShouldNotBeNull();

        view.Label.ShouldBe("We are preparing your replacement");
        view.Location.ShouldBeNull();
        view.ReplacementSteps.ShouldNotBeNull().Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("readyForCollection", Skip = Pending)]
    [InlineData("handedToCustomer", Skip = Pending)]
    public void A_reserved_or_handed_over_unit_shows_its_model_name(string state)
    {
        var (claimRemedy, repair, replacement) = Arrange(state);

        var view = ClaimantRemedyProjection.Project(claimRemedy, repair, null, replacement).ShouldNotBeNull();

        view.ReplacementModelName.ShouldBe(UnitModel.Name);
    }

    [Fact(Skip = Pending)]
    public void No_model_name_is_shown_while_no_unit_is_reserved()
    {
        // The current unit was refused by the customer: the replacement is awaiting a new unit again.
        var replacement = Facts(ReplacementStatus.AwaitingUnit) with { CurrentUnitModel = UnitModel };

        var view = ClaimantRemedyProjection.Project(ReplacementByPolicy(), null, null, replacement).ShouldNotBeNull();

        view.ReplacementModelName.ShouldBeNull();
    }

    [Theory]
    [InlineData("notAvailable", Skip = Pending)]
    [InlineData("cancelledForRefund", Skip = Pending)]
    public void A_replacement_that_is_not_available_or_cancelled_has_no_replacement_steps_location_or_model(string state)
    {
        var (claimRemedy, repair, replacement) = Arrange(state);

        var view = ClaimantRemedyProjection.Project(claimRemedy, repair, null, replacement).ShouldNotBeNull();

        (view.Location, view.ReplacementModelName, view.ReplacementSteps).ShouldBe((null, null, null));
        view.Steps.ShouldBeEmpty();
        view.RepairCentre.ShouldBeNull();
    }

    [Fact(Skip = Pending)]
    public void A_replacement_cancelled_for_a_repair_shows_the_repair_timeline_and_centre()
    {
        var (remedy, repair, replacement) = Arrange("cancelledForRepair");

        var view = ClaimantRemedyProjection.Project(remedy, repair, RepairCentre, replacement).ShouldNotBeNull();

        view.Steps.Select(s => s.Title).ShouldBe(["Handed over", "With repair centre", "Ready for collection", "Collected"]);
        view.RepairCentre.ShouldBe(new ClaimantRepairCentreView(RepairCentre.Name, RepairCentre.Address, RepairCentre.OpeningHours));
        (view.Location, view.ReplacementModelName, view.ReplacementSteps).ShouldBe((null, null, null));
    }

    // ── Staff-only data never reaches the claimant (FR-034) ────────────────────────────────────

    [Theory]
    [InlineData("awaitingReturn", Skip = Pending)]
    [InlineData("awaitingUnitWithReturnMismatch", Skip = Pending)]
    [InlineData("awaitingUnitHeldByRepairCentre", Skip = Pending)]
    [InlineData("readyForCollection", Skip = Pending)]
    [InlineData("handedToCustomer", Skip = Pending)]
    [InlineData("notAvailable", Skip = Pending)]
    public void The_view_has_no_serial_numbers_notes_return_checks_reasons_contacts_or_staff_names(string state)
    {
        var (claimRemedy, repair, replacement) = Arrange(state);

        var view = ClaimantRemedyProjection.Project(claimRemedy, repair, null, replacement).ShouldNotBeNull();

        ShouldHaveNoStaffOnlyData(view, LocationContact, OtherContact);
    }

    [Theory]
    [InlineData(ReplacementReturnRule.BeforeReservation, ReplacementStatus.AwaitingReturn, ReturnState.NotYetReturned, ReturnState.Mismatch, Skip = Pending)]
    [InlineData(ReplacementReturnRule.AtCollection, ReplacementStatus.AwaitingUnit, ReturnState.NotYetReturned, ReturnState.Mismatch, Skip = Pending)]
    [InlineData(ReplacementReturnRule.BeforeReservation, ReplacementStatus.AwaitingUnit, ReturnState.Returned, ReturnState.Accepted, Skip = Pending)]
    [InlineData(ReplacementReturnRule.AtCollection, ReplacementStatus.ReadyForCollection, ReturnState.Returned, ReturnState.Mismatch, Skip = Pending)]
    public void A_return_check_result_does_not_change_the_view(
        ReplacementReturnRule returnRule, ReplacementStatus status, ReturnState passed, ReturnState checkedState)
    {
        var remedy = ReplacementByPolicy();
        var facts = Facts(status, returnRule) with { CurrentUnitModel = UnitModel };

        var withoutCheck = ClaimantRemedyProjection.Project(remedy, null, null, facts with { ReturnState = passed }).ShouldNotBeNull();
        var withCheck = ClaimantRemedyProjection.Project(remedy, null, null, facts with { ReturnState = checkedState }).ShouldNotBeNull();

        JsonSerializer.Serialize(withCheck, JsonSerializerOptions.Web)
            .ShouldBe(JsonSerializer.Serialize(withoutCheck, JsonSerializerOptions.Web));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static TheoryDataRow<string, RemedyType?, string, bool> Row(string state, RemedyType? remedy, string label, bool completed)
        => new(state, remedy, label, completed) { Skip = Pending };

    private static TheoryDataRow<string, string[], string[]> Steps(string state, string[] titles, string[] stepStates)
        => new(state, titles, stepStates) { Skip = Pending };

    /// <summary>
    /// The serialized view (as the API writes it) contains none of <paramref name="staffOnly"/>, no return check result
    /// or return rule value, and no staff-only field names.
    /// </summary>
    private static void ShouldHaveNoStaffOnlyData(ClaimantRemedyView view, params string[] staffOnly)
    {
        var json = JsonSerializer.Serialize(view, JsonSerializerOptions.Web);
        // "Returned" is left out: it is part of the step title "Product returned".
        string[] returnValues = ["NotYetReturned", "HeldByRepairCentre", "Mismatch", "Accepted", .. Enum.GetNames<ReplacementReturnRule>()];
        foreach (var value in staffOnly.Concat(returnValues))
        {
            json.ShouldNotContain(value, Case.Insensitive);
        }

        string[] staffOnlyFields =
        [
            "note", "reason", "contact", "phone", "email", "serial", "serialNumber", "serialSeen", "serialMatches",
            "returnState", "returnRule", "heldByRepairCentre", "actor", "recordedBy", "proposedBy", "decidedBy",
        ];
        var fields = Fields(JsonDocument.Parse(json).RootElement).ToList();
        fields.ShouldNotContain(name => staffOnlyFields.Contains(name, StringComparer.OrdinalIgnoreCase), json);
    }

    private static IEnumerable<string> Fields(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Fields(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Fields),
        _ => [],
    };

    private static ClaimantReplacementFacts Facts(
        ReplacementStatus status,
        ReplacementReturnRule returnRule = ReplacementReturnRule.AtCollection,
        ReturnState returnState = ReturnState.NotYetReturned)
        => new(status, returnRule, returnState, Location, CurrentUnitModel: null);

    private static ClaimRemedy ReplacementByPolicy() => ClaimRemedy.SetByPolicy(
        Guid.CreateVersion7(), Tenant, ClaimId, VersionId, RemedyScenario.CoveredDefect, RemedyType.Replacement,
        [RemedyType.Replacement], ApprovedAt);

    /// <summary>A reviewer changed the remedy away from Replacement (the replacement is then cancelled).</summary>
    private static ClaimRemedy DecidedByReviewer(RemedyType remedy)
    {
        var claimRemedy = ClaimRemedy.AwaitDecision(
            Guid.CreateVersion7(), Tenant, ClaimId, VersionId, RemedyScenario.CoveredDefect,
            [RemedyType.Repair, RemedyType.Replacement, RemedyType.Refund], RemedyPendingReason.PolicyChoice, ApprovedAt);
        claimRemedy.Decide(remedy, ApprovedAt.AddDays(1));
        return claimRemedy;
    }

    /// <summary>
    /// The remedy, the repair and the replacement facts of <paramref name="state"/>. The return rule is
    /// <c>AtCollection</c> unless the state names a return before reservation (<c>awaitingReturn…</c>,
    /// <c>…AfterReturn</c>, <c>…AcceptedReturn</c>); a reserved or handed-over unit is <see cref="UnitModel"/>.
    /// </summary>
    private static (ClaimRemedy Remedy, Repair? Repair, ClaimantReplacementFacts Replacement) Arrange(string state)
    {
        var before = ReplacementReturnRule.BeforeReservation;
        var remedy = ReplacementByPolicy();
        var (facts, completed) = state switch
        {
            "awaitingReturn" => (Facts(ReplacementStatus.AwaitingReturn, before), false),
            "awaitingReturnWithMismatch" => (Facts(ReplacementStatus.AwaitingReturn, before, ReturnState.Mismatch), false),
            "awaitingUnit" => (Facts(ReplacementStatus.AwaitingUnit), false),
            "awaitingUnitAfterReturn" => (Facts(ReplacementStatus.AwaitingUnit, before, ReturnState.Returned), false),
            "awaitingUnitWithReturnMismatch" => (Facts(ReplacementStatus.AwaitingUnit, returnState: ReturnState.Mismatch), false),
            "awaitingUnitAfterAcceptedReturn" => (Facts(ReplacementStatus.AwaitingUnit, before, ReturnState.Accepted), false),
            "awaitingUnitHeldByRepairCentre" => (Facts(ReplacementStatus.AwaitingUnit, returnState: ReturnState.HeldByRepairCentre), false),
            "readyForCollection" => (Reserved(Facts(ReplacementStatus.ReadyForCollection)), false),
            "readyForCollectionAfterReturn" => (Reserved(Facts(ReplacementStatus.ReadyForCollection, before, ReturnState.Returned)), false),
            "readyForCollectionReturnedEarly" => (Reserved(Facts(ReplacementStatus.ReadyForCollection, returnState: ReturnState.Returned)), false),
            "readyForCollectionHeldByRepairCentre" =>
                (Reserved(Facts(ReplacementStatus.ReadyForCollection, returnState: ReturnState.HeldByRepairCentre)), false),
            "handedToCustomer" => (Reserved(Facts(ReplacementStatus.HandedToCustomer, returnState: ReturnState.Returned)), true),
            "handedToCustomerAfterReturn" => (Reserved(Facts(ReplacementStatus.HandedToCustomer, before, ReturnState.Returned)), true),
            "handedToCustomerHeldByRepairCentre" =>
                (Reserved(Facts(ReplacementStatus.HandedToCustomer, returnState: ReturnState.HeldByRepairCentre)), true),
            "notAvailable" => (Facts(ReplacementStatus.NotAvailable), false),
            "cancelledForRefund" or "cancelledForRepair" => (Facts(ReplacementStatus.Cancelled), false),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown state."),
        };

        switch (state)
        {
            case "notAvailable":
                remedy.ReturnToDecision(
                    VersionId, RemedyScenario.ReplacementNotAvailable, [RemedyType.Refund], RemedyPendingReason.ReplacementNotAvailable,
                    ApprovedAt.AddDays(3));
                return (remedy, null, facts);
            case "cancelledForRefund":
                return (DecidedByReviewer(RemedyType.Refund), null, facts);
            case "cancelledForRepair":
                var repair = Repair.Create(Guid.CreateVersion7(), Tenant, ClaimId, RepairCentre.Id, ApprovedAt.AddDays(1));
                return (DecidedByReviewer(RemedyType.Repair), repair, facts);
        }

        if (completed)
        {
            remedy.Complete(RemedyType.Replacement, ApprovedAt.AddDays(5));
        }

        return (remedy, null, facts);
    }

    private static ClaimantReplacementFacts Reserved(ClaimantReplacementFacts facts) => facts with { CurrentUnitModel = UnitModel };
}
