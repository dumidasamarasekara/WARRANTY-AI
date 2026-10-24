using System.Text.Json;
using Warranty.Application.Fulfillment;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Integration;

namespace Warranty.UnitTests.Fulfillment;

/// <summary>
/// The claimant's view of the remedy (002 T236; FR-039 – FR-041, data-model.md "Claimant projection"):
/// <see cref="ClaimantRemedyProjection"/> gives the label of each remedy and repair state, the four Repair timeline
/// steps and the active centre's name, address and opening hours, and never anything staff-only: notes, centre
/// references, failure reasons, contact details or staff names. The states after a failed repair need T247.
/// </summary>
public sealed class ClaimantRemedyProjectionTests
{
    private const string StaffSub = "0b9e7a52-1f2c-4c1a-9a01-a0000000a001";
    private const string StaffNote = "Staff-only note: customer called twice about the delay";
    private const string CentreReference = "JOB-1001";
    private const string CentreContact = "service-desk@springfield-centre.test";
    private const string FailureReason = "Logic board fault came back after replacement";
    private const string ChangeReason = "The customer asked for a centre closer to home.";

    private static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid ClaimId = Guid.Parse("0199b000-0000-7000-8000-000000000236");
    private static readonly Guid VersionId = Guid.Parse("0199b000-0000-7000-8000-0000000002b2");
    private static readonly DateTimeOffset ApprovedAt = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly HandedOverOn = new(2026, 9, 4);

    private static readonly ServiceCenter Centre = ServiceCenter.Create(
        Guid.Parse("33333333-3333-7333-8333-333333333331"), Tenant, Region.NA, "Aurora Service Center Springfield", ["tablet"],
        "1 Main Street, Springfield", "Mon-Fri 09:00-17:00", CentreContact, priority: 10);

    private static readonly ServiceCenter OtherCentre = ServiceCenter.Create(
        Guid.Parse("33333333-3333-7333-8333-333333333332"), Tenant, Region.NA, "Aurora Service Center Shelbyville", ["tablet"],
        "2 Oak Avenue, Shelbyville", "Mon-Sat 09:00-18:00", "555-0101", priority: 20);

    /// <summary>The states of data-model.md "Claimant projection": state → (remedy shown, label, completed).</summary>
    public static IEnumerable<TheoryDataRow<string, RemedyType?, string, bool>> Labels =>
    [
        Row("firstDecision", null, "We are deciding how to resolve your claim", false),
        Row("repairAwaitingHandover", RemedyType.Repair, "Take your product to the repair centre", false),
        Row("repairAtRepairCentre", RemedyType.Repair, "With repair centre", false),
        Row("repairReadyForCollection", RemedyType.Repair, "Ready for collection", false),
        Row("repairCollected", RemedyType.Repair, "Collected", true),
        Row("repairNotPossible", null, "Repair not possible. Your claim is being reviewed.", false),
        // 003 contracts/ui-screens.md §5 replaces 002's "Replacement on its way" (replacements are collected).
        Row("replacementInProgress",RemedyType.Replacement, "We are preparing your replacement", false),
        Row("replacementCompleted", RemedyType.Replacement, "Replacement handed over", true),
        Row("refundInProgress", RemedyType.Refund, "Refund being processed", false),
        Row("refundCompleted", RemedyType.Refund, "Refund completed", true),
    ];

    /// <summary>Repair timeline per status: the step of the current status is <c>current</c>, earlier ones <c>done</c>.</summary>
    public static IEnumerable<TheoryDataRow<string, string[]>> RepairSteps =>
    [
        new("repairAwaitingHandover", ["current", "upcoming", "upcoming", "upcoming"]),
        new("repairAtRepairCentre", ["done", "current", "upcoming", "upcoming"]),
        new("repairReadyForCollection", ["done", "done", "current", "upcoming"]),
        new("repairCollected", ["done", "done", "done", "done"]),
    ];

    // ── Labels ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Labels))]
    public void Each_state_has_its_claimant_label(string state, RemedyType? remedy, string label, bool completed)
    {
        var (claimRemedy, repair) = Arrange(state);

        var view = ClaimantRemedyProjection.Project(claimRemedy, repair, repair is null ? null : Centre).ShouldNotBeNull();

        (view.Remedy, view.Label, view.Completed).ShouldBe((remedy, label, completed));
    }

    [Fact]
    public void A_claim_without_a_remedy_has_no_remedy_view()
        => ClaimantRemedyProjection.Project(null, null, null).ShouldBeNull();

    // ── Repair timeline and centre card ────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(RepairSteps))]
    public void A_repair_shows_the_four_steps_and_the_centre_name_address_and_opening_hours(string state, string[] stepStates)
    {
        var (claimRemedy, repair) = Arrange(state);

        var view = ClaimantRemedyProjection.Project(claimRemedy, repair, Centre).ShouldNotBeNull();

        view.Steps.Select(s => s.Title).ShouldBe(["Handed over", "With repair centre", "Ready for collection", "Collected"]);
        view.Steps.Select(s => s.State).ShouldBe(stepStates);
        view.RepairCentre.ShouldBe(new ClaimantRepairCentreView(Centre.Name, Centre.Address, Centre.OpeningHours));
    }

    [Fact]
    public void A_repair_without_an_assigned_centre_has_no_centre_card()
    {
        var remedy = RepairByPolicy();
        var repair = Repair.Create(Guid.CreateVersion7(), Tenant, ClaimId, suggestedServiceCenterId: null, ApprovedAt);

        var view = ClaimantRemedyProjection.Project(remedy, repair, null).ShouldNotBeNull();

        view.Label.ShouldBe("Take your product to the repair centre");
        view.RepairCentre.ShouldBeNull();
        view.Steps.Count.ShouldBe(4);
    }

    [Theory]
    [InlineData("firstDecision")]
    [InlineData("replacementInProgress")]
    [InlineData("replacementCompleted")]
    [InlineData("refundInProgress")]
    [InlineData("refundCompleted")]
    public void Other_remedies_and_an_open_decision_have_no_steps_and_no_centre(string state)
    {
        var (claimRemedy, repair) = Arrange(state);

        var view = ClaimantRemedyProjection.Project(claimRemedy, repair, null).ShouldNotBeNull();

        view.Steps.ShouldBeEmpty();
        view.RepairCentre.ShouldBeNull();
    }

    // ── Staff-only data never reaches the claimant (FR-041) ────────────────────────────────────

    [Fact]
    public void The_view_has_no_notes_centre_references_contact_details_or_staff_names()
    {
        var remedy = RepairByPolicy();
        var repair = Repair.Create(Guid.CreateVersion7(), Tenant, ClaimId, Centre.Id, ApprovedAt);
        repair.ChangeCentre(OtherCentre.Id, ChangeReason, Report(-1));
        repair.RecordHandover(HandedOverOn, CentreReference, Report(0));
        repair.MarkReady(Report(2));

        var view = ClaimantRemedyProjection.Project(remedy, repair, OtherCentre).ShouldNotBeNull();

        view.RepairCentre.ShouldBe(new ClaimantRepairCentreView(OtherCentre.Name, OtherCentre.Address, OtherCentre.OpeningHours));
        ShouldHaveNoStaffOnlyData(view, StaffNote, CentreReference, ChangeReason, StaffSub, "555-0101", CentreContact);
    }

    [Fact]
    public void The_view_after_a_failed_repair_has_no_failure_reason()
    {
        var (remedy, repair) = Arrange("repairNotPossible");

        var view = ClaimantRemedyProjection.Project(remedy, repair, Centre).ShouldNotBeNull();

        ShouldHaveNoStaffOnlyData(view, FailureReason, StaffNote, CentreReference, StaffSub, CentreContact);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static TheoryDataRow<string, RemedyType?, string, bool> Row(
        string state, RemedyType? remedy, string label, bool completed, string? skip = null)
        => new(state, remedy, label, completed) { Skip = skip };

    /// <summary>The serialized view (as the API writes it) contains none of <paramref name="staffOnly"/> nor staff-only field names.</summary>
    private static void ShouldHaveNoStaffOnlyData(ClaimantRemedyView view, params string[] staffOnly)
    {
        var json = JsonSerializer.Serialize(view, JsonSerializerOptions.Web);
        foreach (var value in staffOnly)
        {
            json.ShouldNotContain(value, Case.Insensitive);
        }

        string[] staffOnlyFields = ["note", "centreReference", "contact", "reason", "failureReason", "actor", "recordedBy", "phone", "email"];
        var fields = Fields(JsonDocument.Parse(json).RootElement).ToList();
        fields.ShouldNotContain(name => staffOnlyFields.Contains(name, StringComparer.OrdinalIgnoreCase), json);
    }

    private static IEnumerable<string> Fields(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Fields(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Fields),
        _ => [],
    };

    /// <summary>A staff report with a staff-only note, <paramref name="day"/> days after the hand-over date.</summary>
    private static RepairReport Report(int day)
    {
        var at = new DateTimeOffset(HandedOverOn.AddDays(day).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        return new RepairReport(at, ReportSource.Phone, StaffSub, StaffNote, at.AddMinutes(5));
    }

    private static ClaimRemedy RepairByPolicy() => ClaimRemedy.SetByPolicy(
        Guid.CreateVersion7(), Tenant, ClaimId, VersionId, RemedyScenario.CoveredDefect, RemedyType.Repair, [RemedyType.Repair], ApprovedAt);

    private static ClaimRemedy SetByPolicy(RemedyType remedy) => ClaimRemedy.SetByPolicy(
        Guid.CreateVersion7(), Tenant, ClaimId, VersionId, RemedyScenario.CoveredDefect, remedy, [remedy], ApprovedAt);

    /// <summary>The remedy and repair of <paramref name="state"/>, reached through the domain methods.</summary>
    private static (ClaimRemedy Remedy, Repair? Repair) Arrange(string state)
    {
        if (state == "firstDecision")
        {
            return (ClaimRemedy.AwaitDecision(
                Guid.CreateVersion7(), Tenant, ClaimId, VersionId, RemedyScenario.CoveredDefect,
                [RemedyType.Refund, RemedyType.Replacement], RemedyPendingReason.PolicyChoice, ApprovedAt), null);
        }

        if (state.StartsWith("replacement", StringComparison.Ordinal) || state.StartsWith("refund", StringComparison.Ordinal))
        {
            var type = state.StartsWith("replacement", StringComparison.Ordinal) ? RemedyType.Replacement : RemedyType.Refund;
            var other = SetByPolicy(type);
            if (state.EndsWith("Completed", StringComparison.Ordinal))
            {
                other.Complete(type, ApprovedAt.AddDays(5));
            }

            return (other, null);
        }

        var remedy = RepairByPolicy();
        var repair = Repair.Create(Guid.CreateVersion7(), Tenant, ClaimId, Centre.Id, ApprovedAt);
        if (state == "repairAwaitingHandover")
        {
            return (remedy, repair);
        }

        repair.RecordHandover(HandedOverOn, CentreReference, Report(0));
        switch (state)
        {
            case "repairReadyForCollection":
                repair.MarkReady(Report(2));
                break;
            case "repairCollected":
                repair.MarkReady(Report(2));
                repair.RecordCollection(Report(3));
                remedy.Complete(RemedyType.Repair, Report(3).RecordedAt);
                break;
            case "repairNotPossible":
                repair.RecordFailure(FailureReason, Report(2));
                remedy.ReturnToDecision(
                    VersionId, RemedyScenario.RepairFailed, [RemedyType.Replacement], RemedyPendingReason.RepairFailedLimit, Report(2).RecordedAt);
                break;
        }

        return (remedy, repair);
    }
}
