namespace Warranty.Domain.Fulfillment;

/// <summary>
/// Who reported a repair update and when (the common part of every <see cref="RepairEvent"/> recorded by staff):
/// <paramref name="ReportedAt"/> is when the repair centre reported it, <paramref name="RecordedAt"/> the server
/// time, <paramref name="Actor"/> the staff <c>sub</c> and <paramref name="Note"/> an optional staff-only note.
/// <paramref name="ActorName"/> is the staff member's display name, kept in the event payload so the staff
/// history can show it (the event's <see cref="RepairEvent.Actor"/> stays the <c>sub</c>).
/// </summary>
public sealed record RepairReport(
    DateTimeOffset ReportedAt,
    ReportSource Source,
    string Actor,
    string? Note,
    DateTimeOffset RecordedAt,
    string? ActorName = null);
