using System.Text.Json.Serialization;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Domain.Common;

namespace Warranty.Application.Audit;

/// <summary><c>SecurityEvent</c> (contracts/rest-api.openapi.yaml): one row of the auditor's security-event list.</summary>
public sealed record SecurityEventView(
    Guid Id,
    DateTimeOffset OccurredAt,
    SecurityEventKind Kind,
    string Actor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Target);

/// <summary><c>SecurityEventPage</c>: a page of the tenant's security events, newest first.</summary>
public sealed record SecurityEventPage(IReadOnlyList<SecurityEventView> Items, int Page, int PageSize, int Total);

/// <summary>
/// The auditor's view of the current tenant's security events (FR-041a). Reads only this tenant's rows
/// (row-level security), never returns operator-only kinds, details or source IP, and projects each row
/// to time, kind, actor and target only. The actor is stored already rendered (a staff display name,
/// "claimant channel" or "adjudication-service"), so it is returned as stored.
/// </summary>
public sealed class SecurityEventQuery(ITenantContext tenant, ISecurityEventReader reader)
{
    public const int DefaultPageSize = 50;

    public const int MaxPageSize = 100;

    public async Task<SecurityEventPage> ListAsync(SecurityEventKind? kind, int page, int pageSize, CancellationToken ct)
    {
        EnsureTenant();
        var result = await reader.ListAsync(kind, page, pageSize, ct);
        var items = result.Events
            .Select(e => new SecurityEventView(e.Id, e.OccurredAt, e.Kind, e.Actor, e.Target))
            .ToList();
        return new SecurityEventPage(items, page, pageSize, result.Total);
    }

    private void EnsureTenant()
    {
        if (!tenant.IsResolved)
        {
            throw new InvalidOperationException("Security-event queries require the tenant of the staff user.");
        }
    }
}
