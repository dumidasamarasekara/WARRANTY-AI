namespace Warranty.Domain.Fulfillment;

/// <summary>
/// Who reported a repair update and when (the common part of every <see cref="RepairEvent"/> recorded by staff):
/// <paramref name="ReportedAt"/> is when the repair centre reported it, <paramref name="RecordedAt"/> the server
/// time, <paramref name="Actor"/> the staff <c>sub</c> and <paramref name="Note"/> an optional staff-only note.
/// </summary>
public sealed record RepairReport(
    DateTimeOffset ReportedAt,
    ReportSource Source,
    string Actor,
    string? Note,
    DateTimeOffset RecordedAt);
