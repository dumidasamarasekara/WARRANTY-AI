namespace Warranty.Domain.Fulfillment;

/// <summary>
/// A staff proposal to hand out an equivalent model (<c>claims.replacement_model_proposals</c>), decided
/// by a reviewer. At most one proposal per replacement is <see cref="ModelProposalState.Pending"/>.
/// Created and changed only through <see cref="Replacement"/>.
/// </summary>
public sealed class ReplacementModelProposal
{
    public const int MaxReasonLength = 1000;

    private ReplacementModelProposal()
    {
        Reason = ProposedBy = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ReplacementId { get; private set; }

    /// <summary>The proposed model; same category, claim value at least the original's (FR-023).</summary>
    public Guid ProductId { get; private set; }

    public string Reason { get; private set; }

    public ModelProposalState State { get; private set; }

    /// <summary>Staff <c>sub</c>.</summary>
    public string ProposedBy { get; private set; }

    public DateTimeOffset ProposedAt { get; private set; }

    /// <summary>Reviewer <c>sub</c>; null while pending.</summary>
    public string? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>Required for <see cref="ModelProposalState.Declined"/>.</summary>
    public string? DecisionReason { get; private set; }

    public bool IsPending => State == ModelProposalState.Pending;

    internal static ReplacementModelProposal Create(
        Guid tenantId, Guid replacementId, Guid productId, string reason, string proposedBy, DateTimeOffset proposedAt)
    {
        if (tenantId == Guid.Empty || replacementId == Guid.Empty || productId == Guid.Empty)
        {
            throw new ArgumentException("Tenant, replacement and product IDs are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(proposedBy);

        return new ReplacementModelProposal
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ReplacementId = replacementId,
            ProductId = productId,
            Reason = RequireReason(reason, nameof(reason)),
            State = ModelProposalState.Pending,
            ProposedBy = proposedBy,
            ProposedAt = proposedAt,
        };
    }

    /// <summary>Records the reviewer's decision on this pending proposal; a decline needs a reason.</summary>
    internal void Decide(ModelProposalState decision, string decidedBy, string? decisionReason, DateTimeOffset decidedAt)
    {
        if (!IsPending)
        {
            throw new InvalidOperationException("The model proposal has already been decided.");
        }

        if (decision == ModelProposalState.Pending || !Enum.IsDefined(decision))
        {
            throw new ArgumentException("A decision approves or declines the proposal.", nameof(decision));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);

        DecisionReason = decision == ModelProposalState.Declined
            ? RequireReason(decisionReason, nameof(decisionReason))
            : OptionalReason(decisionReason, nameof(decisionReason));
        State = decision;
        DecidedBy = decidedBy;
        DecidedAt = decidedAt;
    }

    private static string RequireReason(string? reason, string paramName) =>
        OptionalReason(reason, paramName) ?? throw new ArgumentException("A reason is required.", paramName);

    private static string? OptionalReason(string? reason, string paramName)
    {
        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (trimmed is { Length: > MaxReasonLength })
        {
            throw new ArgumentException($"A reason is at most {MaxReasonLength} characters.", paramName);
        }

        return trimmed;
    }
}
