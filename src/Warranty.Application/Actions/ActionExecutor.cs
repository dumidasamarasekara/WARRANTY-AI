using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Application.Claims;
using Warranty.Application.Fulfillment;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Domain.Review;
using Warranty.Guardrails;
using Warranty.Guardrails.Remedies;
using Warranty.Guardrails.Rules;

namespace Warranty.Application.Actions;

/// <summary>
/// Executes guardrail-issued actions (contracts/agents-and-tools.md "Guardrails → action boundary",
/// research R9/R24):
/// <list type="bullet">
/// <item><see cref="ActionKind.FinalizeApproved"/> → claim <c>Approved</c> by <see cref="DecidedBy.System"/> with the
/// run's claimant explanation, then <c>service_network_lookup</c> → <c>create_repair_request</c> →
/// <c>notify_customer</c> through the simulated integration ports.</item>
/// <item><see cref="ActionKind.FinalizeRejected"/> → claim <c>Rejected</c>, then <c>notify_customer</c>.</item>
/// <item><see cref="ActionKind.EscalateToReview"/> → claim <c>UnderReview</c>.</item>
/// <item><see cref="ActionKind.RequestInformation"/> → claim <c>PendingInformation</c> with the requested items in their
/// claimant-facing form (<see cref="RequestedItemCatalog.ForClaimant(IEnumerable{RequestedItem})"/>; increments <c>auto_info_request_count</c>; the domain refuses a third automatic request).</item>
/// </list>
/// The final explanation is read from the run's persisted recommendation (it already passed
/// <c>CLAIMANT_TEXT_SAFE</c>), never taken from the caller. The notification carries only a template
/// name; the claimant reads the outcome and its explanation from the claim, so no risk data or
/// reference ID reaches the outbox.
/// Reviewer decisions (FR-034 – FR-036) take the same path through <see cref="ExecuteReviewerDecisionAsync"/>:
/// the claim is finalized by <see cref="DecidedBy.Reviewer"/> with the reviewer's screened claimant
/// explanation, or paused for the submitter as a reviewer request.
/// </summary>
public sealed class ActionExecutor(
    ITenantContext tenantContext,
    IClaimRepository claims,
    IAdjudicationRepository adjudication,
    IReviewRepository reviews,
    ICatalogRepository catalog,
    IPolicyRepository policies,
    IServiceNetwork serviceNetwork,
    IRemedyRepository remedies,
    IRepairRepository repairs,
    IRefundService refunds,
    INotificationService notifications,
    IDecisionTrailWriter trail,
    IUnitOfWork unitOfWork,
    RemedyPolicyEvaluator remedyEvaluator,
    TimeProvider time) : IActionExecutor
{
    /// <summary>Notification template for an approved claim.</summary>
    public const string ApprovedTemplate = "claim-approved";

    /// <summary>Notification template for a rejected claim.</summary>
    public const string RejectedTemplate = "claim-rejected";

    /// <summary>Trail actor of guardrail-issued actions: the background adjudication service.</summary>
    private const string Actor = Principals.AdjudicationService;

    public async Task<ActionExecution> ExecuteAsync(ApprovedAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Tenant identity comes only from the trusted scope; an action for any other tenant is refused.
        if (!tenantContext.IsResolved || action.TenantId != tenantContext.TenantId)
        {
            throw new ActionRefusedException(
                $"Action {action.Kind} for claim {action.ClaimId} does not belong to the current tenant scope.");
        }

        var execution = ActionExecution.Executed;
        await unitOfWork.ExecuteInTransactionAsync(async token => { execution = await ApplyAsync(action, token); }, ct);
        return execution;
    }

    public Task ExecuteReviewerDecisionAsync(ReviewDecision decision, CancellationToken ct)
        => ExecuteReviewerDecisionAsync(decision, ReviewerApproval.None, ct);

    public async Task ExecuteReviewerDecisionAsync(ReviewDecision decision, ReviewerApproval approval, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(approval);

        if (!tenantContext.IsResolved || decision.TenantId != tenantContext.TenantId)
        {
            throw new ActionRefusedException(
                $"Reviewer decision on claim {decision.ClaimId} does not belong to the current tenant scope.");
        }

        if (decision.Decision != ReviewDecisionKind.Approve && (approval.Remedy is not null || approval.ProtectionPlanConfirmed is not null))
        {
            throw new ActionRefusedException($"A remedy or a protection plan check comes only with an approval of claim {decision.ClaimId}.");
        }

        await unitOfWork.ExecuteInTransactionAsync(token => ApplyReviewerDecisionAsync(decision, approval, token), ct);
    }

    public async Task ExecuteRemedyDecisionAsync(ReviewerRemedyDecision decision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (!tenantContext.IsResolved || decision.TenantId != tenantContext.TenantId)
        {
            throw new ActionRefusedException(
                $"Remedy decision on claim {decision.ClaimId} does not belong to the current tenant scope.");
        }

        await unitOfWork.ExecuteInTransactionAsync(token => ApplyRemedyDecisionAsync(decision, token), ct);
    }

    /// <summary>
    /// Applies a reviewer's remedy decision (002 research R4): the remedy's policy version is evaluated again for its
    /// scenario and the repair facts, and the decision must stay inside both that evaluation and the remedy's allowed
    /// list; the trail actor is the reviewer's <c>sub</c>.
    /// </summary>
    private async Task ApplyRemedyDecisionAsync(ReviewerRemedyDecision decision, CancellationToken ct)
    {
        var claim = await claims.GetAsync(decision.ClaimId, ct);
        if (claim is null || claim.TenantId != decision.TenantId)
        {
            throw new ActionRefusedException($"Claim {decision.ClaimId} is not visible in the current tenant.");
        }

        // Separation of duties (001 research R29): RecordRemedyDecision refuses first; this is the last line of defense.
        if (claim.WasSubmittedBy(decision.ReviewerSub))
        {
            throw new ActionRefusedException($"Reviewer {decision.ReviewerSub} submitted claim {claim.Id} and may not decide its remedy.");
        }

        var remedy = await remedies.GetAsync(claim.Id, ct);
        if (remedy is null || remedy.TenantId != decision.TenantId)
        {
            throw new InvalidClaimTransitionException($"Claim {claim.Id} has no remedy to decide.");
        }

        var repair = await repairs.GetByClaimAsync(claim.Id, ct);
        if (RemedyDecisionRules.WhyNotDecidable(remedy, repair, decision.Remedy) is { } notDecidable)
        {
            throw new InvalidClaimTransitionException(notDecidable);
        }

        var history = await remedies.GetDecisionsAsync(claim.Id, ct);
        if (!RemedyDecisionRules.IsValidReason(decision.Reason, RemedyDecisionRules.RequiresReason(history)))
        {
            throw new ActionRefusedException($"The remedy decision on claim {claim.Id} needs a reason of 10–1000 characters.");
        }

        // Re-evaluation with the remedy's own policy version, scenario and repair facts (FR-002, FR-010).
        var version = await policies.GetVersionAsync(remedy.PolicyVersionId, ct);
        var product = claim.ProductId is { } productId ? await catalog.GetProductAsync(productId, ct) : null;
        var failedAttempts = repair?.FailedAttempts ?? 0;
        var estimate = repair?.Attempts.LastOrDefault(a => a.EstimateAmount is not null)?.EstimateAmount;
        var evaluation = remedyEvaluator.Evaluate(version?.RemedyRules, remedy.Scenario, failedAttempts, estimate, product?.ClaimValue);
        if (version is null || !evaluation.AllowedRemedies.Contains(decision.Remedy) || !remedy.AllowedRemedies.Contains(decision.Remedy))
        {
            throw new ActionRefusedException(
                $"{decision.Remedy} is not allowed for claim {claim.Id} by its policy ({string.Join(", ", evaluation.AllowedRemedies)}).");
        }

        var now = time.GetUtcNow();
        var previous = remedy.Remedy;
        if (remedy.State == RemedyState.InProgress)
        {
            // A remedy change always passes AwaitingDecision (data-model.md, Remedy state machine).
            remedy.ReturnToDecision(version.Id, remedy.Scenario, evaluation.AllowedRemedies, RemedyPendingReason.PolicyChoice, now);
        }

        remedy.Decide(decision.Remedy, now);
        remedies.AddDecision(RemedyDecision.Decided(
            Guid.CreateVersion7(), claim.TenantId, claim.Id, history.Count + 1, remedy.Scenario, version.Id, evaluation.TableRow,
            evaluation.AllowedRemedies, decision.Remedy, RemedyDecidedBy.Reviewer, decision.ReviewerSub, decision.Reason,
            failedAttempts, estimate, now));

        var actor = decision.ReviewerSub;
        await trail.AppendAsync(
            claim.Id, TrailStep.RemedyDecided, actor,
            previous is { } from
                ? $"Remedy changed from {from} to {decision.Remedy} by reviewer {decision.ReviewerName}."
                : $"Remedy {decision.Remedy} chosen by reviewer {decision.ReviewerName} (version {version.Version}, row {evaluation.TableRow}).",
            new
            {
                remedy = decision.Remedy.ToString(),
                previousRemedy = previous?.ToString(),
                decidedBy = nameof(RemedyDecidedBy.Reviewer),
                reviewer = decision.ReviewerName,
                reason = decision.Reason,
                scenario = remedy.Scenario.ToString(),
                policyVersionId = version.Id,
                tableRow = evaluation.TableRow,
            },
            ct);

        switch (decision.Remedy)
        {
            case RemedyType.Repair when repair is null:
                await CreateRepairAsync(claim, product, actor, now, ct);
                break;
            case RemedyType.Refund:
                await RequestRefundAsync(claim, product, actor, ct);
                break;
        }
    }

    private async Task<ActionExecution> ApplyAsync(ApprovedAction action, CancellationToken ct)
    {
        var claim = await claims.GetAsync(action.ClaimId, ct);
        if (claim is null || claim.TenantId != action.TenantId)
        {
            throw new ActionRefusedException($"Claim {action.ClaimId} is not visible in the current tenant.");
        }

        var record = await adjudication.GetRunRecordAsync(action.RunId, ct);
        if (record is null || record.Run.TenantId != action.TenantId || record.Run.ClaimId != claim.Id)
        {
            throw new ActionRefusedException($"Run {action.RunId} does not belong to claim {claim.Id}.");
        }

        if (record.Run.Round != claim.CurrentRound)
        {
            throw new ActionRefusedException(
                $"Run {action.RunId} evaluated round {record.Run.Round}, but claim {claim.Id} is in round {claim.CurrentRound}.");
        }

        if (record.Guardrails is { } evaluation && KindOf(evaluation.Disposition) != action.Kind)
        {
            throw new ActionRefusedException(
                $"Action {action.Kind} disagrees with the recorded guardrail disposition {evaluation.Disposition} of run {action.RunId}.");
        }

        // A retried job may execute the same action again after its transaction committed.
        if (claim.Status != ClaimStatus.UnderEvaluation && IsAlreadyApplied(action.Kind, claim))
        {
            return ActionExecution.AlreadyApplied;
        }

        var now = time.GetUtcNow();
        var run = new { runId = record.Run.Id, round = record.Run.Round };
        switch (action.Kind)
        {
            case ActionKind.FinalizeApproved:
                claim.FinalizeApproved(ClaimantExplanation(record, AiDecision.Approve), DecidedBy.System, now);
                await trail.AppendAsync(
                    claim.Id, TrailStep.AutoApproved, Actor, "Claim approved automatically: every guardrail check passed.",
                    new { run.runId, run.round, outcome = nameof(FinalOutcome.Approved), decidedBy = nameof(DecidedBy.System) }, ct);
                await SetRemedyAsync(claim, Actor, now, ct);
                await NotifyAsync(claim.Id, ApprovedTemplate, Actor, ct);
                break;

            case ActionKind.FinalizeRejected:
                claim.FinalizeRejected(ClaimantExplanation(record, AiDecision.Reject), DecidedBy.System, now);
                await trail.AppendAsync(
                    claim.Id, TrailStep.AutoRejected, Actor, "Claim rejected automatically: every guardrail check passed.",
                    new { run.runId, run.round, outcome = nameof(FinalOutcome.Rejected), decidedBy = nameof(DecidedBy.System) }, ct);
                await NotifyAsync(claim.Id, RejectedTemplate, Actor, ct);
                break;

            case ActionKind.EscalateToReview:
                claim.EscalateToReview(now);
                var reasons = record.Guardrails?.Reasons.Select(WireName.Of).ToArray() ?? [];
                await trail.AppendAsync(
                    claim.Id, TrailStep.EscalatedToReview, Actor, "Claim routed to human review.",
                    new { run.runId, run.round, reasons }, ct);
                break;

            case ActionKind.RequestInformation:
                // Reasons can come from the model's missingInformation: store claimant-safe text (RequestedItemCatalog).
                claim.RequestInformation(RequestedItemCatalog.ForClaimant(action.RequestedItems), DecidedBy.System, now);
                var items = claim.RequestedItems.Select(i => i.Item).ToArray();
                await trail.AppendAsync(
                    claim.Id, TrailStep.InformationRequested, Actor,
                    $"Information requested from the submitter: {string.Join(", ", items)}.",
                    new { run.runId, run.round, items, autoInfoRequestCount = claim.AutoInfoRequestCount }, ct);
                break;

            default:
                throw new ActionRefusedException($"Unknown action kind {action.Kind}.");
        }

        return ActionExecution.Executed;
    }

    /// <summary>
    /// Applies a reviewer decision. The trail actor is the reviewer's <c>sub</c>; the decision row, the
    /// claim's state change, the trail entries and the simulated integration rows commit together.
    /// </summary>
    private async Task ApplyReviewerDecisionAsync(ReviewDecision decision, ReviewerApproval approval, CancellationToken ct)
    {
        var claim = await claims.GetAsync(decision.ClaimId, ct);
        if (claim is null || claim.TenantId != decision.TenantId)
        {
            throw new ActionRefusedException($"Claim {decision.ClaimId} is not visible in the current tenant.");
        }

        // Separation of duties (research R29): RecordReviewDecision refuses first; this is the last line of defense.
        if (claim.WasSubmittedBy(decision.ReviewerSub))
        {
            throw new ActionRefusedException($"Reviewer {decision.ReviewerSub} submitted claim {claim.Id} and may not decide it.");
        }

        var run = await adjudication.GetLatestRunAsync(claim.Id, ct);
        if (run is null || run.Id != decision.RunId || run.Round != claim.CurrentRound)
        {
            throw new ActionRefusedException($"Reviewer decision on claim {claim.Id} does not apply to the claim's latest run.");
        }

        if (decision.ClaimantExplanation is { } text && !ClaimantTextScreen.Screen(text).IsSafe)
        {
            throw new ActionRefusedException($"The claimant explanation of the decision on claim {claim.Id} failed the claimant text screen.");
        }

        var actor = decision.ReviewerSub;
        var now = decision.DecidedAt;
        switch (decision.Decision)
        {
            case ReviewDecisionKind.Approve:
                if (approval.ProtectionPlanConfirmed is { } confirmed)
                {
                    // 002 research R9: the reviewer's check of the declared plan, stored before the claim leaves review.
                    claim.RecordProtectionPlanCheck(confirmed, now);
                }

                claim.FinalizeApproved(decision.ClaimantExplanation!, DecidedBy.Reviewer, now);
                break;
            case ReviewDecisionKind.Reject:
                claim.FinalizeRejected(decision.ClaimantExplanation!, DecidedBy.Reviewer, now);
                break;
            case ReviewDecisionKind.RequestInformation:
                claim.RequestInformation(decision.RequestedItems, DecidedBy.Reviewer, now);
                break;
            default:
                throw new ActionRefusedException($"Unknown review decision {decision.Decision}.");
        }

        reviews.Add(decision);
        var items = decision.RequestedItems.Select(i => i.Item).ToArray();
        await trail.AppendAsync(
            claim.Id, TrailStep.ReviewerDecided, actor, ReviewerSummary(decision, items),
            new
            {
                decisionId = decision.Id,
                runId = decision.RunId,
                round = run.Round,
                decision = decision.Decision.ToString(),
                overridesAi = decision.OverridesAi,
                reviewer = decision.ReviewerName,
                justification = decision.Justification,
                claimantExplanation = decision.ClaimantExplanation,
                requestedItems = items,
                status = claim.Status.ToString(),
            },
            ct);

        switch (decision.Decision)
        {
            case ReviewDecisionKind.Approve:
                await SetRemedyAsync(claim, actor, now, ct, approval.Remedy, decision.ReviewerSub, decision.ReviewerName);
                await NotifyAsync(claim.Id, ApprovedTemplate, actor, ct);
                break;
            case ReviewDecisionKind.Reject:
                await NotifyAsync(claim.Id, RejectedTemplate, actor, ct);
                break;
        }
    }

    private static string ReviewerSummary(ReviewDecision decision, string[] items) => decision.Decision switch
    {
        ReviewDecisionKind.Approve => $"Claim approved by reviewer {decision.ReviewerName}{(decision.OverridesAi ? ", overriding the AI recommendation" : string.Empty)}.",
        ReviewDecisionKind.Reject => $"Claim rejected by reviewer {decision.ReviewerName}{(decision.OverridesAi ? ", overriding the AI recommendation" : string.Empty)}.",
        _ => $"Reviewer {decision.ReviewerName} requested information from the submitter: {string.Join(", ", items)}.",
    };

    /// <summary>
    /// Sets the remedy of a just-approved claim in the approval transaction (002 research R3, FR-009 – FR-011):
    /// the applicable policy version's remedy rules are evaluated for the initial scenario (research R13). One
    /// remedy is set by policy — Repair creates a <see cref="Repair"/> at the first eligible centre serving the
    /// claim (it replaces the 001 simulated repair request, research R8), Refund asks the refund port once for the
    /// claim value. A choice, a missing rule or missing facts leave the remedy awaiting a reviewer; the claim's
    /// Approved outcome is never held back. A reviewer approving the claim may choose the remedy in the same request
    /// (FR-013, research R4): it must be one the evaluation allows, and a choice between remedies is then decided by the
    /// reviewer as the remedy's first decision.
    /// </summary>
    private async Task SetRemedyAsync(
        Claim claim, string actor, DateTimeOffset now, CancellationToken ct,
        RemedyType? chosen = null, string? reviewerSub = null, string? reviewerName = null)
    {
        var product = claim.ProductId is { } productId ? await catalog.GetProductAsync(productId, ct) : null;
        var version = RemedyQueries.ApplicableVersion(await policies.GetVersionsAsync(ct), claim, product);
        if (version is null)
        {
            if (chosen is not null)
            {
                throw new ActionRefusedException($"Claim {claim.Id} has no single applicable policy version to allow {chosen}.");
            }

            // A remedy row needs its policy version; without exactly one the reviewers resolve it from the claim.
            await trail.AppendAsync(
                claim.Id, TrailStep.RemedyRecommended, actor,
                "No single applicable policy version: the remedy awaits a reviewer.",
                new { policyVersionId = (Guid?)null, pendingReason = WireName.Of(RemedyPendingReason.NoRemedyRule) }, ct);
            return;
        }

        const RemedyScenario scenario = RemedyScenario.CoveredDefect;
        var evaluation = remedyEvaluator.Evaluate(version.RemedyRules, scenario, claimValue: product?.ClaimValue);
        if (chosen is { } choice && !evaluation.AllowedRemedies.Contains(choice))
        {
            throw new ActionRefusedException(
                $"{choice} is not allowed for claim {claim.Id} by its policy ({string.Join(", ", evaluation.AllowedRemedies)}).");
        }

        var seq = (await remedies.GetDecisionsAsync(claim.Id, ct)).Count;
        remedies.AddDecision(RemedyDecision.Evaluated(
            Guid.CreateVersion7(), claim.TenantId, claim.Id, ++seq, scenario, version.Id, evaluation.TableRow,
            evaluation.AllowedRemedies, failedAttempts: 0, estimateAmount: null, now));
        await trail.AppendAsync(
            claim.Id, TrailStep.RemedyRecommended, actor, RecommendationSummary(evaluation, version),
            new
            {
                policyVersionId = version.Id,
                policyVersion = version.Version,
                scenario = scenario.ToString(),
                tableRow = evaluation.TableRow,
                allowedRemedies = evaluation.AllowedRemedies.Select(r => r.ToString()).ToArray(),
                recommendation = evaluation.SingleRemedy?.ToString(),
                pendingReason = evaluation.PendingReason is { } reason ? WireName.Of(reason) : null,
            },
            ct);

        RemedyType remedy;
        if (evaluation.AutomaticRemedy is { } automatic && (chosen is null || chosen == automatic))
        {
            remedy = automatic;
            remedies.Add(ClaimRemedy.SetByPolicy(
                Guid.CreateVersion7(), claim.TenantId, claim.Id, version.Id, scenario, remedy, evaluation.AllowedRemedies, now));
            remedies.AddDecision(RemedyDecision.Decided(
                Guid.CreateVersion7(), claim.TenantId, claim.Id, ++seq, scenario, version.Id, evaluation.TableRow,
                evaluation.AllowedRemedies, remedy, RemedyDecidedBy.Policy, RemedyDecision.SystemActor, reason: null,
                failedAttempts: 0, estimateAmount: null, now));
            await trail.AppendAsync(
                claim.Id, TrailStep.RemedyDecided, actor,
                $"Remedy {remedy} set by policy (version {version.Version}, row {evaluation.TableRow}).",
                new { remedy = remedy.ToString(), decidedBy = nameof(RemedyDecidedBy.Policy), policyVersionId = version.Id, tableRow = evaluation.TableRow },
                ct);
        }
        else
        {
            var awaiting = ClaimRemedy.AwaitDecision(
                Guid.CreateVersion7(), claim.TenantId, claim.Id, version.Id, scenario, evaluation.AllowedRemedies,
                evaluation.PendingReason ?? RemedyPendingReason.NoRemedyRule, now);
            remedies.Add(awaiting);
            if (chosen is not { } reviewerChoice || reviewerSub is null)
            {
                return;
            }

            // The approving reviewer decides the choice in the same request: the remedy's first decision, no reason needed.
            remedy = reviewerChoice;
            awaiting.Decide(remedy, now);
            remedies.AddDecision(RemedyDecision.Decided(
                Guid.CreateVersion7(), claim.TenantId, claim.Id, ++seq, scenario, version.Id, evaluation.TableRow,
                evaluation.AllowedRemedies, remedy, RemedyDecidedBy.Reviewer, reviewerSub, reason: null,
                failedAttempts: 0, estimateAmount: null, now));
            await trail.AppendAsync(
                claim.Id, TrailStep.RemedyDecided, actor,
                $"Remedy {remedy} chosen by reviewer {reviewerName ?? reviewerSub} with the approval (version {version.Version}, row {evaluation.TableRow}).",
                new
                {
                    remedy = remedy.ToString(),
                    decidedBy = nameof(RemedyDecidedBy.Reviewer),
                    reviewer = reviewerName,
                    policyVersionId = version.Id,
                    tableRow = evaluation.TableRow,
                },
                ct);
        }

        switch (remedy)
        {
            case RemedyType.Repair:
                await CreateRepairAsync(claim, product, actor, now, ct);
                break;
            case RemedyType.Refund:
                await RequestRefundAsync(claim, product, actor, ct);
                break;
        }
    }

    /// <summary><c>request_refund</c>: the refund port is asked once for the claim value (research R10).</summary>
    private async Task RequestRefundAsync(Claim claim, Product? product, string actor, CancellationToken ct)
    {
        var amount = product?.ClaimValue ?? claim.PurchasePrice;
        var refundRequestId = await refunds.RequestRefundAsync(claim.Id, amount, ct);
        await trail.AppendAsync(
            claim.Id, TrailStep.ActionExecuted, actor, "Simulated refund requested.",
            new { action = "request_refund", refundRequestId, amount }, ct);
    }

    /// <summary>The repair, suggested to the first active centre (priority, then name) that serves the claim; staff choose one otherwise.</summary>
    private async Task CreateRepairAsync(Claim claim, Product? product, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var centre = claim.Region is { } region && product is not null
            ? (await serviceNetwork.ListEligibleAsync(region, product.Category, ct)).FirstOrDefault(c => c.ServesClaim)
            : null;
        var repair = Repair.Create(Guid.CreateVersion7(), claim.TenantId, claim.Id, centre?.Id, now);
        repairs.Add(repair);
        if (centre is not null)
        {
            await trail.AppendAsync(
                claim.Id, TrailStep.RepairCentreAssigned, actor, $"Repair centre suggested: {centre.Name}.",
                new { repairId = repair.Id, serviceCenterId = centre.Id, serviceCenterName = centre.Name, assignment = nameof(HandoverAssignment.Suggested) },
                ct);
        }
    }

    private static string RecommendationSummary(RemedyEvaluation evaluation, PolicyVersion version)
    {
        var basis = $"policy version {version.Version}, row {evaluation.TableRow}";
        return evaluation switch
        {
            { AutomaticRemedy: { } remedy } => $"Remedy {remedy} from the policy ({basis}).",
            { AllowedRemedies.Count: > 0 } => $"The policy allows {string.Join(" or ", evaluation.AllowedRemedies)} ({basis}): a reviewer decides.",
            _ => $"The policy gives no remedy for this claim ({basis}): a reviewer decides.",
        };
    }

    /// <summary><c>notify_customer</c>: a simulated outbox row naming the template, never the explanation text.</summary>
    private async Task NotifyAsync(Guid claimId, string template, string actor, CancellationToken ct)
    {
        var notificationId = await notifications.EnqueueAsync(claimId, template, ct);
        await trail.AppendAsync(
            claimId, TrailStep.ActionExecuted, actor, "Simulated customer notification queued.",
            new { action = "notify_customer", notificationId, template }, ct);
    }

    /// <summary>The run's claimant explanation; only a valid recommendation with the matching decision can finalize.</summary>
    private static string ClaimantExplanation(RunRecord record, AiDecision expected)
    {
        var recommendation = record.Recommendation;
        if (recommendation is null
            || recommendation.RunId != record.Run.Id
            || !recommendation.IsValid
            || recommendation.Decision != expected
            || string.IsNullOrWhiteSpace(recommendation.ClaimantExplanation))
        {
            throw new ActionRefusedException(
                $"Run {record.Run.Id} has no valid {expected} recommendation with a claimant explanation to finalize with.");
        }

        return recommendation.ClaimantExplanation;
    }

    private static bool IsAlreadyApplied(ActionKind kind, Claim claim) => kind switch
    {
        ActionKind.FinalizeApproved => claim.Status == ClaimStatus.Approved && claim.FinalDecidedBy == DecidedBy.System,
        ActionKind.FinalizeRejected => claim.Status == ClaimStatus.Rejected && claim.FinalDecidedBy == DecidedBy.System,
        ActionKind.EscalateToReview => claim.Status == ClaimStatus.UnderReview,
        ActionKind.RequestInformation => claim.Status == ClaimStatus.PendingInformation,
        _ => false,
    };

    private static ActionKind KindOf(Disposition disposition) => disposition switch
    {
        Disposition.AutoApprove => ActionKind.FinalizeApproved,
        Disposition.AutoReject => ActionKind.FinalizeRejected,
        Disposition.RequestInformation => ActionKind.RequestInformation,
        _ => ActionKind.EscalateToReview,
    };
}
