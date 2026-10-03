using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;

namespace Warranty.Domain.Review;

/// <summary>
/// A reviewer's decision on an escalated claim (FR-034 – FR-036). The internal justification and the
/// claimant-facing explanation are separate fields; the justification is never shown to claimants.
/// Screening the claimant text for disclosure terms is a guardrail rule (ClaimantTextScreen, R25).
/// </summary>
public sealed class ReviewDecision
{
    public const int MinJustificationLength = 10;
    public const int MaxJustificationLength = 2000;
    public const int MinClaimantExplanationLength = 20;
    public const int MaxClaimantExplanationLength = 1500;

    private ReviewDecision()
    {
        ReviewerSub = ReviewerName = string.Empty;
        RequestedItems = [];
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ClaimId { get; private set; }

    /// <summary>The decision applies to the latest run of the claim.</summary>
    public Guid RunId { get; private set; }

    public string ReviewerSub { get; private set; }

    public string ReviewerName { get; private set; }

    public ReviewDecisionKind Decision { get; private set; }

    /// <summary>Internal, staff-only reason.</summary>
    public string? Justification { get; private set; }

    /// <summary>Shown to the claimant as the outcome explanation (approve/reject only).</summary>
    public string? ClaimantExplanation { get; private set; }

    public IReadOnlyList<RequestedItem> RequestedItems { get; private set; }

    /// <summary>True only when the decision differs from a valid APPROVE/REJECT recommendation (FR-035).</summary>
    public bool OverridesAi { get; private set; }

    public DateTimeOffset DecidedAt { get; private set; }

    /// <summary>
    /// Creates a decision after enforcing FR-035/FR-036: a justification when overriding a valid AI
    /// decision or rejecting; a claimant explanation for approve/reject and none for a request for
    /// information; requested items exactly for a request for information.
    /// </summary>
    public static ReviewDecision Create(
        Guid id,
        Guid tenantId,
        Guid claimId,
        Guid runId,
        string reviewerSub,
        string reviewerName,
        ReviewDecisionKind decision,
        string? justification,
        string? claimantExplanation,
        IEnumerable<RequestedItem>? requestedItems,
        Recommendation? latestRecommendation,
        DateTimeOffset decidedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerSub);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerName);

        var overridesAi = latestRecommendation?.IsOverriddenBy(decision) ?? false;
        var reason = Trimmed(justification);
        var message = Trimmed(claimantExplanation);
        var items = requestedItems?.ToArray() ?? [];

        if ((overridesAi || decision == ReviewDecisionKind.Reject) && reason is null)
        {
            throw new ReviewDecisionValidationException("justification", "A justification is required when overriding the AI recommendation or rejecting a claim.");
        }

        if (reason is not null && reason.Length is < MinJustificationLength or > MaxJustificationLength)
        {
            throw new ReviewDecisionValidationException("justification", $"Justification must be {MinJustificationLength}–{MaxJustificationLength} characters.");
        }

        if (decision == ReviewDecisionKind.RequestInformation)
        {
            if (message is not null)
            {
                throw new ReviewDecisionValidationException("claimantExplanation", "A request for information has no claimant explanation.");
            }

            if (items.Length == 0)
            {
                throw new ReviewDecisionValidationException("requestedItems", "A request for information must list the requested items.");
            }
        }
        else
        {
            if (message is null || message.Length is < MinClaimantExplanationLength or > MaxClaimantExplanationLength)
            {
                throw new ReviewDecisionValidationException(
                    "claimantExplanation",
                    $"A claimant explanation of {MinClaimantExplanationLength}–{MaxClaimantExplanationLength} characters is required to approve or reject.");
            }

            if (items.Length > 0)
            {
                throw new ReviewDecisionValidationException("requestedItems", "Requested items are only allowed when requesting information.");
            }
        }

        return new ReviewDecision
        {
            Id = id,
            TenantId = tenantId,
            ClaimId = claimId,
            RunId = runId,
            ReviewerSub = reviewerSub,
            ReviewerName = reviewerName.Trim(),
            Decision = decision,
            Justification = reason,
            ClaimantExplanation = message,
            RequestedItems = items,
            OverridesAi = overridesAi,
            DecidedAt = decidedAt,
        };
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A reviewer decision request that breaks FR-035/FR-036; maps to a 400 naming the field.</summary>
public sealed class ReviewDecisionValidationException : ArgumentException
{
    public ReviewDecisionValidationException()
    {
        Field = string.Empty;
    }

    public ReviewDecisionValidationException(string message)
        : base(message)
    {
        Field = string.Empty;
    }

    public ReviewDecisionValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Field = string.Empty;
    }

    public ReviewDecisionValidationException(string field, string message)
        : base(message)
    {
        Field = field;
    }

    public string Field { get; }
}
