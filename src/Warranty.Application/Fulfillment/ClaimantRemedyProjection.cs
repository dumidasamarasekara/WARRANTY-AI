using System.Text.Json.Serialization;
using Warranty.Domain.Catalog;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Integration;

namespace Warranty.Application.Fulfillment;

/// <summary>
/// <c>ClaimantClaimView.remedy</c> (002 contracts, <c>ClaimantRemedyView</c>; FR-039 – FR-041): the remedy, the claimant
/// label of data-model.md "Claimant projection", whether it is completed, the repair centre's public details and, for
/// Repair, the four timeline steps. It never contains notes, centre references, failure reasons, contact details,
/// staff names or risk data. For Replacement (003 FR-032 – FR-034) it adds the active location, the reserved unit's
/// model name and the replacement timeline steps, and never serial numbers, return check results or reasons.
/// </summary>
public sealed record ClaimantRemedyView(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RemedyType? Remedy,
    string Label,
    bool Completed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClaimantRepairCentreView? RepairCentre,
    IReadOnlyList<ClaimantRemedyStep> Steps,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClaimantRepairCentreView? Location = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplacementModelName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ClaimantRemedyStep>? ReplacementSteps = null);

/// <summary>The active repair centre as the claimant sees it: name, address and opening hours only.</summary>
public sealed record ClaimantRepairCentreView(string Name, string? Address, string? OpeningHours);

/// <summary>One step of the Repair timeline: <see cref="Title"/> is one of <see cref="ClaimantRemedyProjection.StepTitles"/>;
/// <see cref="State"/> is <c>done</c>, <c>current</c> or <c>upcoming</c>.</summary>
public sealed record ClaimantRemedyStep(string Title, string State);

/// <summary>
/// What the claimant projection reads of the claim's replacement (003 data-model.md "Claimant projection"):
/// its status, return rule and return state, the active location (null when none is assigned) and the model of
/// the current unit (null before the first reservation).
/// </summary>
public sealed record ClaimantReplacementFacts(
    ReplacementStatus Status,
    ReplacementReturnRule ReturnRule,
    ReturnState ReturnState,
    ServiceCenter? ActiveLocation,
    Product? CurrentUnitModel);

/// <summary>
/// Computes <see cref="ClaimantRemedyView"/> from the claim's remedy, its repair and the active hand-over's centre
/// (data-model.md "Claimant projection").
/// </summary>
public static class ClaimantRemedyProjection
{
    /// <summary>The four Repair timeline steps, in order.</summary>
    public static readonly IReadOnlyList<string> StepTitles = ["Handed over", "With repair centre", "Ready for collection", "Collected"];

    /// <summary>
    /// The claimant's view of the remedy, or null when the claim has no remedy. <paramref name="activeCentre"/> is the
    /// centre of the repair's active hand-over (null when none is assigned or the remedy is not Repair).
    /// </summary>
    public static ClaimantRemedyView? Project(ClaimRemedy? remedy, Repair? repair, ServiceCenter? activeCentre)
    {
        if (remedy is null)
        {
            return null;
        }

        if (remedy.State == RemedyState.AwaitingDecision)
        {
            var label = repair is { Status: RepairStatus.RepairNotPossible }
                ? "Repair not possible. Your claim is being reviewed."
                : "We are deciding how to resolve your claim";
            return new ClaimantRemedyView(null, label, Completed: false, RepairCentre: null, Steps: []);
        }

        var completed = remedy.State == RemedyState.Completed;
        return remedy.Remedy switch
        {
            RemedyType.Repair => ForRepair(repair, activeCentre, completed),
            RemedyType.Replacement => new ClaimantRemedyView(
                RemedyType.Replacement, completed ? "Replacement handed over" : "Replacement on its way", completed, null, []),
            RemedyType.Refund => new ClaimantRemedyView(
                RemedyType.Refund, completed ? "Refund completed" : "Refund being processed", completed, null, []),
            _ => throw new InvalidOperationException($"Unknown remedy {remedy.Remedy}."),
        };
    }

    /// <summary>
    /// The claimant's view of the remedy including the claim's replacement (null when it has none); the replacement
    /// labels, <c>location</c>, <c>replacementModelName</c> and <c>replacementSteps</c> are added in T330.
    /// </summary>
    public static ClaimantRemedyView? Project(
        ClaimRemedy? remedy, Repair? repair, ServiceCenter? activeCentre, ClaimantReplacementFacts? replacement)
        => throw new NotImplementedException("The replacement claimant projection is added in T330.");

    private static ClaimantRemedyView ForRepair(Repair? repair, ServiceCenter? activeCentre, bool completed)
    {
        // The step of the current status: 0 Handed over (awaiting it), 1 With repair centre, 2 Ready for collection,
        // 3 Collected (then every step is done). A repair that is not possible while the remedy is still in progress
        // (a new attempt follows) stays "With repair centre".
        var status = completed ? RepairStatus.Collected : repair?.Status ?? RepairStatus.AwaitingHandover;
        var (current, label) = status switch
        {
            RepairStatus.AwaitingHandover => (0, "Take your product to the repair centre"),
            RepairStatus.AtRepairCentre or RepairStatus.RepairNotPossible => (1, "With repair centre"),
            RepairStatus.ReadyForCollection => (2, "Ready for collection"),
            RepairStatus.Collected => (StepTitles.Count, "Collected"),
            _ => throw new InvalidOperationException($"Unknown repair status {status}."),
        };

        var steps = StepTitles
            .Select((title, i) => new ClaimantRemedyStep(title, i < current ? "done" : i == current ? "current" : "upcoming"))
            .ToList();
        var centre = activeCentre is null
            ? null
            : new ClaimantRepairCentreView(activeCentre.Name, activeCentre.Address, activeCentre.OpeningHours);
        return new ClaimantRemedyView(RemedyType.Repair, label, completed, centre, steps);
    }
}
