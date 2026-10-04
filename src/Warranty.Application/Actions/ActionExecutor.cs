using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Guardrails;

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
/// <item><see cref="ActionKind.RequestInformation"/> → claim <c>PendingInformation</c> with the requested items
/// (increments <c>auto_info_request_count</c>; the domain refuses a third automatic request).</item>
/// </list>
/// The final explanation is read from the run's persisted recommendation (it already passed
/// <c>CLAIMANT_TEXT_SAFE</c>), never taken from the caller. The notification carries only a template
/// name; the claimant reads the outcome and its explanation from the claim, so no risk data or
/// reference ID reaches the outbox.
/// </summary>
public sealed class ActionExecutor(
    ITenantContext tenantContext,
    IClaimRepository claims,
    IAdjudicationRepository adjudication,
    ICatalogRepository catalog,
    IServiceNetwork serviceNetwork,
    IRepairRequestService repairRequests,
    INotificationService notifications,
    IDecisionTrailWriter trail,
    IUnitOfWork unitOfWork,
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
                await CreateRepairRequestAsync(claim, ct);
                await NotifyAsync(claim.Id, ApprovedTemplate, ct);
                break;

            case ActionKind.FinalizeRejected:
                claim.FinalizeRejected(ClaimantExplanation(record, AiDecision.Reject), DecidedBy.System, now);
                await trail.AppendAsync(
                    claim.Id, TrailStep.AutoRejected, Actor, "Claim rejected automatically: every guardrail check passed.",
                    new { run.runId, run.round, outcome = nameof(FinalOutcome.Rejected), decidedBy = nameof(DecidedBy.System) }, ct);
                await NotifyAsync(claim.Id, RejectedTemplate, ct);
                break;

            case ActionKind.EscalateToReview:
                claim.EscalateToReview(now);
                var reasons = record.Guardrails?.Reasons.Select(WireName.Of).ToArray() ?? [];
                await trail.AppendAsync(
                    claim.Id, TrailStep.EscalatedToReview, Actor, "Claim routed to human review.",
                    new { run.runId, run.round, reasons }, ct);
                break;

            case ActionKind.RequestInformation:
                claim.RequestInformation(action.RequestedItems, DecidedBy.System, now);
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
    /// <c>service_network_lookup</c> → <c>create_repair_request</c>. The approval stands without a
    /// matching service center; the trail then records that no repair request was created.
    /// </summary>
    private async Task CreateRepairRequestAsync(Claim claim, CancellationToken ct)
    {
        var product = claim.ProductId is { } productId ? await catalog.GetProductAsync(productId, ct) : null;
        var center = claim.Region is { } region && product is not null
            ? await serviceNetwork.FindServiceCenterAsync(region, product.Category, ct)
            : null;

        if (center is null)
        {
            await trail.AppendAsync(
                claim.Id, TrailStep.ActionExecuted, Actor,
                "No service center matches the claim's region and product category; no repair request was created.",
                new { action = "service_network_lookup", serviceCenterFound = false }, ct);
            return;
        }

        var repairRequestId = await repairRequests.CreateAsync(claim.Id, center.Id, ct);
        await trail.AppendAsync(
            claim.Id, TrailStep.ActionExecuted, Actor, $"Simulated repair request created at {center.Name}.",
            new { action = "create_repair_request", repairRequestId, serviceCenterId = center.Id }, ct);
    }

    /// <summary><c>notify_customer</c>: a simulated outbox row naming the template, never the explanation text.</summary>
    private async Task NotifyAsync(Guid claimId, string template, CancellationToken ct)
    {
        var notificationId = await notifications.EnqueueAsync(claimId, template, ct);
        await trail.AppendAsync(
            claimId, TrailStep.ActionExecuted, Actor, "Simulated customer notification queued.",
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
