using Warranty.Domain.Review;
using Warranty.Guardrails;

namespace Warranty.Application.Actions;

/// <summary>
/// The single path to consequential operations (constitution II, research R9): finalizing a claim,
/// requesting information, escalating to review, creating a repair request and notifying the customer.
/// It accepts only a guardrail-issued <see cref="ApprovedAction"/>, which no AI output can create.
/// </summary>
public interface IActionExecutor
{
    /// <summary>
    /// Applies <paramref name="action"/> to its claim in the current tenant scope, in one transaction
    /// (it joins the caller's transaction when one is open): the claim's state change, its decision
    /// trail entries and the simulated integration rows commit together.
    /// </summary>
    /// <remarks>
    /// Safe to retry: when the claim already shows the action's result for the same round (e.g. a job
    /// retried after the transaction committed), nothing is changed or written again and
    /// <see cref="ActionExecution.AlreadyApplied"/> is returned.
    /// </remarks>
    /// <exception cref="ActionRefusedException">
    /// The action belongs to another tenant than the current scope, its run is not the claim's current
    /// round, or the run's recommendation cannot finalize the claim. Nothing is changed.
    /// </exception>
    /// <exception cref="Warranty.Domain.Claims.InvalidClaimTransitionException">
    /// The claim state machine refuses the change (e.g. a third automatic information request, or a
    /// claim that already reached a different outcome). Nothing is changed.
    /// </exception>
    Task<ActionExecution> ExecuteAsync(ApprovedAction action, CancellationToken ct);

    /// <summary>
    /// Records a reviewer's validated <paramref name="decision"/> on an <c>UnderReview</c> claim of the
    /// current tenant and applies it, in one transaction: <c>Approve</c>/<c>Reject</c> finalize the claim
    /// by <see cref="Warranty.Domain.Claims.DecidedBy.Reviewer"/> with the reviewer's claimant explanation
    /// (approval also creates the simulated repair request; both notify the customer);
    /// <c>RequestInformation</c> pauses it for the submitter and marks it reviewer-requested. The trail
    /// records <c>ReviewerDecided</c> with the justification and the claimant explanation.
    /// </summary>
    /// <remarks>
    /// <see cref="Warranty.Application.Review.RecordReviewDecision"/> enforces the request rules first; the
    /// executor re-checks the ones that protect the claim (tenant, latest run, separation of duties,
    /// claimant text screen) and fails closed.
    /// </remarks>
    /// <exception cref="ActionRefusedException">
    /// The decision belongs to another tenant, its run is not the claim's latest, the reviewer submitted
    /// the claim, or the claimant explanation fails the screen. Nothing is changed.
    /// </exception>
    /// <exception cref="Warranty.Domain.Claims.InvalidClaimTransitionException">The claim is not under review. Nothing is changed.</exception>
    /// <exception cref="Warranty.Application.Abstractions.Persistence.ConcurrencyConflictException">
    /// The claim changed since it was read. Nothing is changed.
    /// </exception>
    Task ExecuteReviewerDecisionAsync(ReviewDecision decision, CancellationToken ct);

    /// <summary>
    /// As <see cref="ExecuteReviewerDecisionAsync(ReviewDecision, CancellationToken)"/>, with the 002 approval fields
    /// (FR-013, research R4, R9): an approval records the reviewer's protection plan check and sets the chosen remedy,
    /// which must be one the policy allows; without a choice the policy's single remedy is set as for an automatic approval.
    /// </summary>
    /// <exception cref="ActionRefusedException">
    /// As above, or the chosen remedy is not allowed by the claim's policy version. Nothing is changed.
    /// </exception>
    Task ExecuteReviewerDecisionAsync(ReviewDecision decision, ReviewerApproval approval, CancellationToken ct);

    /// <summary>
    /// Applies a reviewer's validated remedy <paramref name="decision"/> to an approved claim of the current tenant, in one
    /// transaction (002 FR-002, FR-013, FR-015, research R4): the remedy's policy version is evaluated again for its
    /// scenario and repair facts, the chosen remedy must be allowed by both that evaluation and the remedy, and every
    /// decision after the first remedy needs a reason. The remedy becomes the chosen one (a change of a remedy in progress
    /// passes <see cref="Warranty.Domain.Fulfillment.RemedyState.AwaitingDecision"/>), a <c>Decided</c> remedy decision
    /// by the reviewer and a <c>RemedyDecided</c> trail entry are written, and the side effect runs: Refund asks the
    /// refund port once for the claim value; Repair creates the repair at the suggested centre when the claim has none.
    /// The claim's final outcome never changes (FR-016).
    /// </summary>
    /// <exception cref="ActionRefusedException">
    /// The decision belongs to another tenant, the reviewer submitted the claim, the remedy is not allowed by the
    /// re-evaluation, or a required reason is missing. Nothing is changed.
    /// </exception>
    /// <exception cref="Warranty.Domain.Claims.InvalidClaimTransitionException">
    /// The claim has no remedy, or its remedy cannot be decided now (completed, or a repair already handed over). Nothing is changed.
    /// </exception>
    /// <exception cref="Warranty.Application.Abstractions.Persistence.ConcurrencyConflictException">
    /// The remedy changed since it was read. Nothing is changed.
    /// </exception>
    Task ExecuteRemedyDecisionAsync(ReviewerRemedyDecision decision, CancellationToken ct);
}

/// <summary>The 002 fields of a reviewer approval: the chosen remedy and the protection plan check (null when not given).</summary>
public sealed record ReviewerApproval(Warranty.Domain.Fulfillment.RemedyType? Remedy, bool? ProtectionPlanConfirmed)
{
    /// <summary>No remedy chosen and no protection plan check.</summary>
    public static ReviewerApproval None { get; } = new(null, null);
}

/// <summary>A reviewer's remedy decision as validated by <see cref="Warranty.Application.Fulfillment.RecordRemedyDecision"/>.</summary>
public sealed record ReviewerRemedyDecision(
    Guid TenantId,
    Guid ClaimId,
    string ReviewerSub,
    string ReviewerName,
    Warranty.Domain.Fulfillment.RemedyType Remedy,
    string? Reason);

/// <summary>What <see cref="IActionExecutor.ExecuteAsync"/> did.</summary>
public enum ActionExecution
{
    /// <summary>The claim changed state and the trail entries and integration rows were written.</summary>
    Executed,

    /// <summary>The claim already showed the action's result for this round; nothing was written (safe retry).</summary>
    AlreadyApplied,
}

/// <summary>An <see cref="ApprovedAction"/> the executor will not apply (fail closed); nothing was changed.</summary>
public sealed class ActionRefusedException : InvalidOperationException
{
    public ActionRefusedException()
    {
    }

    public ActionRefusedException(string message)
        : base(message)
    {
    }

    public ActionRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
