namespace Warranty.Domain.Fulfillment;

/// <summary>
/// Assignment of a replacement to one replacement location (<c>claims.replacement_locations</c>), a
/// service centre that issues replacements. At most one location per replacement is active; a change or
/// move deactivates it and adds a new one. Created and changed only through <see cref="Replacement"/>.
/// </summary>
public sealed class ReplacementLocation
{
    /// <summary>The <see cref="RecordedBy"/> value of a location suggested by the system.</summary>
    public const string SystemRecorder = "system";

    public const int MaxReasonLength = 1000;

    private ReplacementLocation()
    {
        RecordedBy = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ReplacementId { get; private set; }

    public Guid ServiceCenterId { get; private set; }

    public LocationAssignment Assignment { get; private set; }

    /// <summary>Required for <see cref="LocationAssignment.ChosenByStaff"/> and <see cref="LocationAssignment.Moved"/>.</summary>
    public string? Reason { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Staff <c>sub</c>, or <see cref="SystemRecorder"/> for a suggestion.</summary>
    public string RecordedBy { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    internal static ReplacementLocation Create(
        Guid tenantId,
        Guid replacementId,
        Guid serviceCenterId,
        LocationAssignment assignment,
        string? reason,
        string recordedBy,
        DateTimeOffset recordedAt)
    {
        if (tenantId == Guid.Empty || replacementId == Guid.Empty || serviceCenterId == Guid.Empty)
        {
            throw new ArgumentException("Tenant, replacement and service-centre IDs are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(recordedBy);
        if (assignment != LocationAssignment.Suggested && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException($"A reason is required for a {assignment} location.", nameof(reason));
        }

        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (trimmed is { Length: > MaxReasonLength })
        {
            throw new ArgumentException($"A location reason is at most {MaxReasonLength} characters.", nameof(reason));
        }

        return new ReplacementLocation
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ReplacementId = replacementId,
            ServiceCenterId = serviceCenterId,
            Assignment = assignment,
            Reason = trimmed,
            IsActive = true,
            RecordedBy = recordedBy,
            RecordedAt = recordedAt,
        };
    }

    /// <summary>Ends this assignment (a location change or move replaces it).</summary>
    internal void Deactivate() => IsActive = false;
}
