using Warranty.Domain.Common;

namespace Warranty.Domain.Claims;

/// <summary>
/// A warranty claim on one product unit. State changes go only through the methods below, which
/// enforce the claim state machine and the loop limits (data-model.md, research R24). Only the
/// ActionExecutor calls the finalize / request-information methods (constitution II).
/// </summary>
public sealed class Claim
{
    /// <summary>Automatic requests for more information allowed before a claim must go to a reviewer (FR-010).</summary>
    public const int MaxAutomaticInformationRequests = 2;

    /// <summary>The submitted-by value for claims from a tenant's claimant channel.</summary>
    public const string ClaimantSubmitter = "claimant";

    public const int MinDescriptionLength = 20;

    public const int MaxDescriptionLength = 4000;

    private List<RequestedItem> _requestedItems = [];

    private Claim()
    {
        Reference = SubmittedBy = ProductModelCode = SerialNumber = PurchasePlace = ProblemDescription = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Reference { get; private set; }

    public ClaimChannel Channel { get; private set; }

    /// <summary>Staff <c>sub</c> of the submitting claims agent, or <see cref="ClaimantSubmitter"/>.</summary>
    public string SubmittedBy { get; private set; }

    public Guid CustomerId { get; private set; }

    /// <summary>Normalized email given at submission; used for claimant access (FR-037a).</summary>
    public string? ContactEmail { get; private set; }

    /// <summary>Normalized phone given at submission; used for claimant access (FR-037a).</summary>
    public string? ContactPhone { get; private set; }

    public string ProductModelCode { get; private set; }

    /// <summary>Null when the product/serial is not in the tenant's catalog (FR-003).</summary>
    public Guid? ProductId { get; private set; }

    public string SerialNumber { get; private set; }

    public DateOnly PurchaseDate { get; private set; }

    public string PurchasePlace { get; private set; }

    public decimal PurchasePrice { get; private set; }

    /// <summary>Region of purchase, else the customer's; null when it cannot be determined.</summary>
    public Region? Region { get; private set; }

    /// <summary>Untrusted claimant text; evidence only, never instructions (FR-019).</summary>
    public string ProblemDescription { get; private set; }

    /// <summary>The submission date (UTC); coverage windows are evaluated against it.</summary>
    public DateOnly ClaimDate { get; private set; }

    public ClaimStatus Status { get; private set; }

    public FinalOutcome? FinalOutcome { get; private set; }

    public DecidedBy? FinalDecidedBy { get; private set; }

    /// <summary>Claimant-facing explanation of the final outcome; never contains risk signals.</summary>
    public string? FinalExplanation { get; private set; }

    public DateTimeOffset? FinalizedAt { get; private set; }

    public IReadOnlyList<RequestedItem> RequestedItems => _requestedItems;

    public int CurrentRound { get; private set; }

    public int AutoInfoRequestCount { get; private set; }

    /// <summary>Set once a reviewer requests information; never cleared (research R24).</summary>
    public bool ReviewerInfoRequested { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Claim Submit(
        Guid id,
        Guid tenantId,
        string reference,
        ClaimChannel channel,
        string submittedBy,
        Guid customerId,
        string? contactEmail,
        string? contactPhone,
        string productModelCode,
        Guid? productId,
        string serialNumber,
        DateOnly purchaseDate,
        string purchasePlace,
        decimal purchasePrice,
        Region? region,
        string problemDescription,
        DateTimeOffset submittedAt)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || customerId == Guid.Empty)
        {
            throw new ArgumentException("Claim, tenant and customer IDs are required.");
        }

        if (!ClaimReference.IsValid(reference))
        {
            throw new ArgumentException($"'{reference}' is not a valid claim reference.", nameof(reference));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(submittedBy);
        if (channel == ClaimChannel.ClaimantPortal && submittedBy != ClaimantSubmitter)
        {
            throw new ArgumentException("Claimant-channel claims are submitted by 'claimant'.", nameof(submittedBy));
        }

        if (string.IsNullOrWhiteSpace(contactEmail) && string.IsNullOrWhiteSpace(contactPhone))
        {
            throw new ArgumentException("An email address or phone number is required for claimant access.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(productModelCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(purchasePlace);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(purchasePrice);

        var description = problemDescription?.Trim() ?? string.Empty;
        if (description.Length is < MinDescriptionLength or > MaxDescriptionLength)
        {
            throw new ArgumentException(
                $"Problem description must be {MinDescriptionLength}–{MaxDescriptionLength} characters.", nameof(problemDescription));
        }

        var claimDate = DateOnly.FromDateTime(submittedAt.UtcDateTime);
        if (purchaseDate > claimDate)
        {
            throw new ArgumentException("Purchase date cannot be in the future or after the claim date.", nameof(purchaseDate));
        }

        return new Claim
        {
            Id = id,
            TenantId = tenantId,
            Reference = reference,
            Channel = channel,
            SubmittedBy = submittedBy,
            CustomerId = customerId,
            ContactEmail = string.IsNullOrWhiteSpace(contactEmail) ? null : ContactNormalizer.NormalizeEmail(contactEmail),
            ContactPhone = string.IsNullOrWhiteSpace(contactPhone) ? null : ContactNormalizer.NormalizePhone(contactPhone),
            ProductModelCode = productModelCode.Trim().ToUpperInvariant(),
            ProductId = productId,
            SerialNumber = serialNumber.Trim().ToUpperInvariant(),
            PurchaseDate = purchaseDate,
            PurchasePlace = purchasePlace.Trim(),
            PurchasePrice = decimal.Round(purchasePrice, 2),
            Region = region,
            ProblemDescription = description,
            ClaimDate = claimDate,
            Status = ClaimStatus.Submitted,
            CurrentRound = 1,
            CreatedAt = submittedAt,
            UpdatedAt = submittedAt,
        };
    }

    /// <summary>The worker picked up the round's job. Idempotent so a retried or resumed job can call it again.</summary>
    public void StartEvaluation(DateTimeOffset now)
    {
        if (Status is not (ClaimStatus.Submitted or ClaimStatus.UnderEvaluation))
        {
            throw new InvalidClaimTransitionException(Status, "start evaluating");
        }

        Status = ClaimStatus.UnderEvaluation;
        UpdatedAt = now;
    }

    /// <summary>
    /// Pauses the claim for the submitter. A <see cref="DecidedBy.System"/> request counts toward the
    /// automatic limit and is refused once it is reached; a reviewer request marks the claim so later
    /// rounds always return to review (FR-010, FR-034, research R24).
    /// </summary>
    public void RequestInformation(IEnumerable<RequestedItem> items, DecidedBy requestedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("At least one requested item is required.", nameof(items));
        }

        if (requestedBy == DecidedBy.System)
        {
            if (Status != ClaimStatus.UnderEvaluation)
            {
                throw new InvalidClaimTransitionException(Status, "automatically request information on");
            }

            if (AutoInfoRequestCount >= MaxAutomaticInformationRequests)
            {
                throw new InvalidClaimTransitionException(
                    $"The claim already had {MaxAutomaticInformationRequests} automatic information requests; it must go to human review.");
            }

            AutoInfoRequestCount++;
        }
        else
        {
            if (Status != ClaimStatus.UnderReview)
            {
                throw new InvalidClaimTransitionException(Status, "request information as a reviewer on");
            }

            ReviewerInfoRequested = true;
        }

        _requestedItems = list;
        Status = ClaimStatus.PendingInformation;
        UpdatedAt = now;
    }

    public void EscalateToReview(DateTimeOffset now)
    {
        if (Status != ClaimStatus.UnderEvaluation)
        {
            throw new InvalidClaimTransitionException(Status, "escalate");
        }

        Status = ClaimStatus.UnderReview;
        UpdatedAt = now;
    }

    public void FinalizeApproved(string explanation, DecidedBy decidedBy, DateTimeOffset now)
        => Finalize(Claims.FinalOutcome.Approved, explanation, decidedBy, now);

    public void FinalizeRejected(string explanation, DecidedBy decidedBy, DateTimeOffset now)
        => Finalize(Claims.FinalOutcome.Rejected, explanation, decidedBy, now);

    /// <summary>The submitter supplied requested items: a new round starts and is re-evaluated in full.</summary>
    public void AddSupplement(DateTimeOffset now)
    {
        if (Status != ClaimStatus.PendingInformation)
        {
            throw new InvalidClaimTransitionException(Status, "supplement");
        }

        CurrentRound++;
        _requestedItems = [];
        Status = ClaimStatus.UnderEvaluation;
        UpdatedAt = now;
    }

    /// <summary>Whether the given staff subject submitted this claim (separation of duties, research R29).</summary>
    public bool WasSubmittedBy(string staffSubject)
        => Channel == ClaimChannel.AgentPortal && string.Equals(SubmittedBy, staffSubject, StringComparison.Ordinal);

    private void Finalize(FinalOutcome outcome, string explanation, DecidedBy decidedBy, DateTimeOffset now)
    {
        var expected = decidedBy == DecidedBy.System ? ClaimStatus.UnderEvaluation : ClaimStatus.UnderReview;
        if (Status != expected)
        {
            throw new InvalidClaimTransitionException(
                Status, $"finalize as {outcome} by {decidedBy}");
        }

        if (string.IsNullOrWhiteSpace(explanation))
        {
            throw new ArgumentException("A claimant-facing explanation is required for a final outcome.", nameof(explanation));
        }

        Status = outcome == Claims.FinalOutcome.Approved ? ClaimStatus.Approved : ClaimStatus.Rejected;
        FinalOutcome = outcome;
        FinalDecidedBy = decidedBy;
        FinalExplanation = explanation.Trim();
        FinalizedAt = now;
        _requestedItems = [];
        UpdatedAt = now;
    }
}
