using Warranty.Domain.Claims;

namespace Warranty.Domain.Fulfillment;

/// <summary>
/// Staff record that a Replacement or Refund remedy was carried out (<c>claims.remedy_completions</c>,
/// one per claim). A completed Repair has no row; its completion is the repair's <c>Collected</c>
/// status. A Refund needs the refund reference from the refund option (FR-035).
/// </summary>
public sealed class RemedyCompletion
{
    private RemedyCompletion()
    {
        RecordedBy = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ClaimId { get; private set; }

    /// <summary><see cref="RemedyType.Replacement"/> or <see cref="RemedyType.Refund"/>; equals the current remedy.</summary>
    public RemedyType Remedy { get; private set; }

    /// <summary>Not in the future.</summary>
    public DateOnly CompletedOn { get; private set; }

    /// <summary>Required for a Refund (the refund reference).</summary>
    public string? Reference { get; private set; }

    /// <summary>ID returned by the refund service, when one was requested.</summary>
    public Guid? RefundRequestId { get; private set; }

    /// <summary>Staff <c>sub</c>.</summary>
    public string RecordedBy { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    /// <summary>
    /// Records the completion of <paramref name="claimRemedy"/>, which must be an in-progress
    /// Replacement or Refund. The caller then calls <see cref="ClaimRemedy.Complete"/> in the same
    /// transaction.
    /// </summary>
    public static RemedyCompletion Record(
        Guid id,
        ClaimRemedy claimRemedy,
        DateOnly completedOn,
        string? reference,
        Guid? refundRequestId,
        string recordedBy,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(claimRemedy);
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A completion ID is required.", nameof(id));
        }

        if (claimRemedy.State != RemedyState.InProgress)
        {
            throw new InvalidClaimTransitionException($"Cannot record a completion for a remedy in state {claimRemedy.State}.");
        }

        if (claimRemedy.Remedy is not (RemedyType.Replacement or RemedyType.Refund))
        {
            throw new InvalidClaimTransitionException(
                $"Only a Replacement or Refund remedy is completed by a record; the current remedy is {claimRemedy.Remedy}.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(recordedBy);
        if (completedOn > DateOnly.FromDateTime(recordedAt.UtcDateTime))
        {
            throw new ArgumentOutOfRangeException(nameof(completedOn), "The completion date cannot be in the future.");
        }

        var trimmedReference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        var remedy = claimRemedy.Remedy.Value;
        if (remedy == RemedyType.Refund && trimmedReference is null)
        {
            throw new ArgumentException("A refund completion requires the refund reference.", nameof(reference));
        }

        if (remedy != RemedyType.Refund && refundRequestId is not null)
        {
            throw new ArgumentException("Only a refund completion has a refund request.", nameof(refundRequestId));
        }

        return new RemedyCompletion
        {
            Id = id,
            TenantId = claimRemedy.TenantId,
            ClaimId = claimRemedy.ClaimId,
            Remedy = remedy,
            CompletedOn = completedOn,
            Reference = trimmedReference,
            RefundRequestId = refundRequestId,
            RecordedBy = recordedBy,
            RecordedAt = recordedAt,
        };
    }
}
