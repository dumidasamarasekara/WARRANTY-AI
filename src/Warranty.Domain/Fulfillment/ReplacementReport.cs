namespace Warranty.Domain.Fulfillment;

/// <summary>
/// Who reported a replacement update and when (the common part of every <see cref="ReplacementEvent"/> recorded by staff,
/// 003 FR-015): <paramref name="ReportedAt"/> is when the location reported it, <paramref name="RecordedAt"/> the server
/// time, <paramref name="Actor"/> the staff <c>sub</c>, <paramref name="Role"/> the staff role the event is recorded with
/// (role rules of the data-model.md state machine) and <paramref name="Note"/> an optional staff-only note.
/// <paramref name="ActorName"/> is the staff member's display name, kept in the event payload so the staff history can
/// show it (the event's <see cref="ReplacementEvent.Actor"/> stays the <c>sub</c>).
/// </summary>
public sealed record ReplacementReport(
    DateTimeOffset ReportedAt,
    ReportSource Source,
    string Actor,
    StaffRole Role,
    string? Note,
    DateTimeOffset RecordedAt,
    string? ActorName = null);
