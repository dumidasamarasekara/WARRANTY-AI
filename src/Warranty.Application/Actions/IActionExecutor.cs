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
}

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
