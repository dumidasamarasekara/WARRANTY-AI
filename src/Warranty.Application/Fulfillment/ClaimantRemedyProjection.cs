using System.Text.Json.Serialization;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Integration;

namespace Warranty.Application.Fulfillment;

/// <summary>
/// <c>ClaimantClaimView.remedy</c> (002 contracts, <c>ClaimantRemedyView</c>; FR-039 – FR-041): the remedy, the claimant
/// label of data-model.md "Claimant projection", whether it is completed, the repair centre's public details and, for
/// Repair, the four timeline steps. It never contains notes, centre references, failure reasons, contact details,
/// staff names or risk data.
/// </summary>
public sealed record ClaimantRemedyView(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RemedyType? Remedy,
    string Label,
    bool Completed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClaimantRepairCentreView? RepairCentre,
    IReadOnlyList<ClaimantRemedyStep> Steps);

/// <summary>The active repair centre as the claimant sees it: name, address and opening hours only.</summary>
public sealed record ClaimantRepairCentreView(string Name, string? Address, string? OpeningHours);

/// <summary>One step of the Repair timeline: <see cref="Title"/> is one of <see cref="ClaimantRemedyProjection.StepTitles"/>;
/// <see cref="State"/> is <c>done</c>, <c>current</c> or <c>upcoming</c>.</summary>
public sealed record ClaimantRemedyStep(string Title, string State);

/// <summary>
/// Computes <see cref="ClaimantRemedyView"/> from the claim's remedy, its repair and the active hand-over's centre
/// (data-model.md "Claimant projection"). Pending T242.
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
        => throw new NotImplementedException($"Pending T242 ({remedy}, {repair}, {activeCentre}).");
}
