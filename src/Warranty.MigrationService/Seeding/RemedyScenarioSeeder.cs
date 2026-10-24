using Microsoft.EntityFrameworkCore;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Integrations;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Crm;
using Warranty.Domain.Fulfillment;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Remedies;
using Warranty.Infrastructure.Audit;
using Warranty.Infrastructure.Persistence;

namespace Warranty.MigrationService.Seeding;

/// <summary>
/// Seeds the approved claims of <c>remedy-scenarios.json</c> (002 quickstart §2) inside the tenant's scope,
/// after the tenant's policies are indexed (the remedy needs their policy versions). Each claim is approved
/// through the same domain methods as the <c>ActionExecutor</c>, and its remedy is not read from the seed but
/// evaluated by the one <see cref="RemedyPolicyEvaluator"/> from the remedy rules of the claim's applicable
/// policy version for the initial scenario (research R3, R13): an evaluation row, then — for a single remedy —
/// <see cref="ClaimRemedy.SetByPolicy"/> with a policy decision row and, for Repair, a <see cref="Repair"/>
/// suggested to the first eligible centre (<see cref="IServiceNetwork.ListEligibleAsync"/>); otherwise
/// <see cref="ClaimRemedy.AwaitDecision"/>. Trail entries carry their historical times and are marked
/// <c>seededHistory</c> like the history claims. A claim with a <see cref="DecidedRemedySeed"/> instead gets the remedy a reviewer
/// decided after a re-evaluation of its own policy version (T251, R11). Claims whose reference exists are skipped, so every start is
/// idempotent; each claim is written in one transaction.
/// </summary>
internal sealed class RemedyScenarioSeeder(
    WarrantyDbContext db,
    ITenantContext tenantContext,
    IServiceNetwork serviceNetwork,
    RemedyPolicyEvaluator evaluator,
    TimeProvider time,
    ILogger<RemedyScenarioSeeder> logger)
{
    /// <summary>The initial scenario of an approved claim without a confirmed protection plan (research R13).</summary>
    private const RemedyScenario InitialScenario = RemedyScenario.CoveredDefect;

    public Task SeedAsync(TenantSeedSet seed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seed);
        return SeedClaimsAsync(seed.Tenant, seed.RemedyScenarios, "remedy scenario", onRemedy: null, ct);
    }

    /// <summary>
    /// Seeds <paramref name="scenarios"/> of <paramref name="tenant"/> (claims whose reference exists are skipped).
    /// <paramref name="onRemedy"/> runs inside each claim's transaction once its remedy is set, so a remedy's
    /// fulfilment record (003's <see cref="Replacement"/>) is written with it, as the executor does (003 research R3).
    /// </summary>
    internal async Task SeedClaimsAsync(
        TenantSeed tenant, IReadOnlyList<RemedyScenarioClaimSeed> scenarios, string kind, SeededRemedyHandler? onRemedy, CancellationToken ct)
    {
        if (scenarios.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        using var scope = TenantContextScope.Begin(tenant.Id, tenant.Slug, SeedingPipeline.Principal);
        var versions = await db.PolicyVersions.AsNoTracking().ToListAsync(ct);
        var added = 0;
        foreach (var scenario in scenarios)
        {
            var reference = ClaimReference.Normalize(scenario.Reference);
            if (await db.Claims.AnyAsync(c => c.Reference == reference, ct))
            {
                continue;
            }

            await SeedClaimAsync(scenario, reference, versions, now, onRemedy, ct);
            added++;
        }

        logger.LogInformation(
            "Seeded tenant {Tenant}: {Added} new {Kind} claims of {Total}", tenant.Slug, added, kind, scenarios.Count);
    }

    private async Task SeedClaimAsync(
        RemedyScenarioClaimSeed scenario,
        string reference,
        IReadOnlyList<PolicyVersion> versions,
        DateTimeOffset now,
        SeededRemedyHandler? onRemedy,
        CancellationToken ct)
    {
        var email = ContactNormalizer.NormalizeEmail(scenario.CustomerEmail);
        var customer = await db.Customers.SingleAsync(c => c.Email == email, ct);
        var product = await db.Products.SingleAsync(p => p.ModelCode == Product.NormalizeModelCode(scenario.ModelCode), ct);
        var submittedAt = scenario.SubmittedAt(now);
        var approvedAt = scenario.ApprovedAt(now);
        var tenantId = tenantContext.TenantId;

        var claim = Claim.Submit(
            scenario.Id, tenantId, reference, ClaimChannel.ClaimantPortal, Claim.ClaimantSubmitter, customer.Id,
            customer.Email, customer.Phone, product.ModelCode, product.Id, scenario.SerialNumber, scenario.PurchaseDate(now),
            scenario.PurchasePlace, scenario.PurchasePrice, scenario.Region, scenario.ProblemDescription, submittedAt);
        claim.StartEvaluation(submittedAt);
        if (scenario.ApprovedBy == DecidedBy.Reviewer)
        {
            claim.EscalateToReview(submittedAt);
        }

        claim.FinalizeApproved(scenario.Explanation, scenario.ApprovedBy, approvedAt);

        // The remedy comes from the claim's own policy version, never from the seed file.
        var applicable = versions.Where(v => v.AppliesTo(claim.PurchaseDate, scenario.Region, product.Category)).ToList();
        if (applicable.Count != 1)
        {
            throw new InvalidDataException(
                $"Remedy scenario {scenario.Scenario}: {applicable.Count} policy versions apply to the claim; a seeded scenario needs exactly one.");
        }

        var version = applicable[0];
        if (scenario.DecidedRemedy is { } decided)
        {
            await SeedDecidedRemedyAsync(claim, product, scenario, decided, version, approvedAt, decided.DecidedAt(now), onRemedy, ct);
            return;
        }

        var evaluation = evaluator.Evaluate(version.RemedyRules, InitialScenario, claimValue: product.ClaimValue);
        if (evaluation.AutomaticRemedy == RemedyType.Refund)
        {
            // A refund is a consequential action only the ActionExecutor may request (constitution II).
            throw new InvalidDataException(
                $"Remedy scenario {scenario.Scenario}: policy {version.Id} sets Refund automatically; the seed cannot request a refund.");
        }

        var centre = evaluation.AutomaticRemedy == RemedyType.Repair
            ? (await serviceNetwork.ListEligibleAsync(scenario.Region, product.Category, ServiceCenterPurpose.Repair, ct)).FirstOrDefault(c => c.ServesClaim)
            : null;

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(
            async token =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                db.Claims.Add(claim);
                await AppendAsync(claim.Id, submittedAt, TrailStep.ClaimSubmitted, "Seeded history: claim submitted.", new { seededHistory = true, scenario.Scenario }, token);
                await AppendApprovalAsync(claim, scenario, approvedAt, token);
                await AppendAsync(
                    claim.Id, approvedAt, TrailStep.RemedyRecommended, $"Seeded history: {RecommendationSummary(evaluation, version)}",
                    new
                    {
                        seededHistory = true,
                        policyVersionId = version.Id,
                        policyVersion = version.Version,
                        scenario = InitialScenario.ToString(),
                        tableRow = evaluation.TableRow,
                        allowedRemedies = evaluation.AllowedRemedies.Select(r => r.ToString()).ToArray(),
                        recommendation = evaluation.SingleRemedy?.ToString(),
                        pendingReason = evaluation.PendingReason is { } reason ? WireName.Of(reason) : null,
                    },
                    token);

                var seq = 0;
                db.RemedyDecisions.Add(RemedyDecision.Evaluated(
                    Guid.CreateVersion7(), tenantId, claim.Id, ++seq, InitialScenario, version.Id, evaluation.TableRow,
                    evaluation.AllowedRemedies, failedAttempts: 0, estimateAmount: null, approvedAt));

                if (evaluation.AutomaticRemedy is not { } remedy)
                {
                    db.ClaimRemedies.Add(ClaimRemedy.AwaitDecision(
                        Guid.CreateVersion7(), tenantId, claim.Id, version.Id, InitialScenario, evaluation.AllowedRemedies,
                        evaluation.PendingReason ?? RemedyPendingReason.NoRemedyRule, approvedAt));
                    await db.SaveChangesAsync(token);
                    await transaction.CommitAsync(token);
                    return;
                }

                db.ClaimRemedies.Add(ClaimRemedy.SetByPolicy(
                    Guid.CreateVersion7(), tenantId, claim.Id, version.Id, InitialScenario, remedy, evaluation.AllowedRemedies, approvedAt));
                db.RemedyDecisions.Add(RemedyDecision.Decided(
                    Guid.CreateVersion7(), tenantId, claim.Id, ++seq, InitialScenario, version.Id, evaluation.TableRow,
                    evaluation.AllowedRemedies, remedy, RemedyDecidedBy.Policy, RemedyDecision.SystemActor, reason: null,
                    failedAttempts: 0, estimateAmount: null, approvedAt));
                await AppendAsync(
                    claim.Id, approvedAt, TrailStep.RemedyDecided,
                    $"Seeded history: remedy {remedy} set by policy (version {version.Version}, row {evaluation.TableRow}).",
                    new { seededHistory = true, remedy = remedy.ToString(), decidedBy = nameof(RemedyDecidedBy.Policy), policyVersionId = version.Id, tableRow = evaluation.TableRow },
                    token);

                if (remedy == RemedyType.Repair)
                {
                    var repair = Repair.Create(Guid.CreateVersion7(), tenantId, claim.Id, centre?.Id, approvedAt);
                    db.Repairs.Add(repair);
                    if (centre is not null)
                    {
                        await AppendAsync(
                            claim.Id, approvedAt, TrailStep.RepairCentreAssigned, $"Seeded history: repair centre suggested: {centre.Name}.",
                            new
                            {
                                seededHistory = true,
                                repairId = repair.Id,
                                serviceCenterId = centre.Id,
                                serviceCenterName = centre.Name,
                                assignment = nameof(HandoverAssignment.Suggested),
                            },
                            token);
                    }
                }

                if (onRemedy is not null)
                {
                    await onRemedy(new SeededRemedy(claim, product, version, remedy, approvedAt), token);
                }

                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            },
            ct);
    }

    /// <summary>
    /// A claim whose remedy a reviewer decided after a re-evaluation (<see cref="DecidedRemedySeed"/>): the evaluation of
    /// the claim's own policy version for the seeded scenario must need a reviewer and allow the seeded remedy. One
    /// evaluation row and one reviewer decision row; the remedy is in progress. A decided Refund is history only: the
    /// seed requests no refund (constitution II), so its completion carries no refund request ID.
    /// </summary>
    private async Task SeedDecidedRemedyAsync(
        Claim claim,
        Product product,
        RemedyScenarioClaimSeed scenario,
        DecidedRemedySeed decided,
        PolicyVersion version,
        DateTimeOffset approvedAt,
        DateTimeOffset decidedAt,
        SeededRemedyHandler? onRemedy,
        CancellationToken ct)
    {
        var evaluation = evaluator.Evaluate(
            version.RemedyRules, decided.Scenario, decided.FailedAttempts, decided.EstimateAmount, product.ClaimValue);
        if (!evaluation.NeedsReviewer || !evaluation.AllowedRemedies.Contains(decided.Remedy))
        {
            throw new InvalidDataException(
                $"Remedy scenario {scenario.Scenario}: policy {version.Id} does not let a reviewer decide {decided.Remedy} "
                + $"for {decided.Scenario} (allowed: {string.Join(", ", evaluation.AllowedRemedies)}).");
        }

        var tenantId = tenantContext.TenantId;
        var remedy = ClaimRemedy.AwaitDecision(
            Guid.CreateVersion7(), tenantId, claim.Id, version.Id, decided.Scenario, evaluation.AllowedRemedies,
            evaluation.PendingReason ?? RemedyPendingReason.NoRemedyRule, decidedAt);
        remedy.Decide(decided.Remedy, decidedAt);

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(
            async token =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                db.Claims.Add(claim);
                await AppendAsync(claim.Id, claim.CreatedAt, TrailStep.ClaimSubmitted, "Seeded history: claim submitted.", new { seededHistory = true, scenario.Scenario }, token);
                await AppendApprovalAsync(claim, scenario, approvedAt, token);
                await AppendAsync(
                    claim.Id, decidedAt, TrailStep.RemedyRecommended, $"Seeded history: {RecommendationSummary(evaluation, version)}",
                    new
                    {
                        seededHistory = true,
                        policyVersionId = version.Id,
                        policyVersion = version.Version,
                        scenario = decided.Scenario.ToString(),
                        tableRow = evaluation.TableRow,
                        allowedRemedies = evaluation.AllowedRemedies.Select(r => r.ToString()).ToArray(),
                        recommendation = evaluation.SingleRemedy?.ToString(),
                        pendingReason = evaluation.PendingReason is { } reason ? WireName.Of(reason) : null,
                        failedAttempts = decided.FailedAttempts,
                        estimateAmount = decided.EstimateAmount,
                    },
                    token);

                db.RemedyDecisions.Add(RemedyDecision.Evaluated(
                    Guid.CreateVersion7(), tenantId, claim.Id, 1, decided.Scenario, version.Id, evaluation.TableRow,
                    evaluation.AllowedRemedies, decided.FailedAttempts, decided.EstimateAmount, decidedAt));
                db.ClaimRemedies.Add(remedy);
                db.RemedyDecisions.Add(RemedyDecision.Decided(
                    Guid.CreateVersion7(), tenantId, claim.Id, 2, decided.Scenario, version.Id, evaluation.TableRow,
                    evaluation.AllowedRemedies, decided.Remedy, RemedyDecidedBy.Reviewer, TenantSeeder.HistoryActor, decided.Reason,
                    decided.FailedAttempts, decided.EstimateAmount, decidedAt));
                await AppendAsync(
                    claim.Id, decidedAt, TrailStep.RemedyDecided,
                    $"Seeded history: remedy {decided.Remedy} decided by a reviewer (version {version.Version}, row {evaluation.TableRow}).",
                    new
                    {
                        seededHistory = true,
                        remedy = decided.Remedy.ToString(),
                        decidedBy = nameof(RemedyDecidedBy.Reviewer),
                        reason = decided.Reason.Trim(),
                        policyVersionId = version.Id,
                        tableRow = evaluation.TableRow,
                    },
                    token);

                if (onRemedy is not null)
                {
                    await onRemedy(new SeededRemedy(claim, product, version, decided.Remedy, decidedAt), token);
                }

                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            },
            ct);
    }

    private Task AppendApprovalAsync(Claim claim, RemedyScenarioClaimSeed scenario, DateTimeOffset approvedAt, CancellationToken ct)
    {
        var marker = new { seededHistory = true, scenario.Scenario, outcome = nameof(FinalOutcome.Approved), decidedBy = scenario.ApprovedBy.ToString() };
        return scenario.ApprovedBy == DecidedBy.System
            ? AppendAsync(claim.Id, approvedAt, TrailStep.AutoApproved, "Seeded history: claim approved automatically.", marker, ct)
            : AppendAsync(claim.Id, approvedAt, TrailStep.ReviewerDecided, "Seeded history: approved by a reviewer.", marker, ct);
    }

    /// <summary>Appends a seeded-history trail entry at its historical time <paramref name="at"/>.</summary>
    internal Task AppendAsync(Guid claimId, DateTimeOffset at, TrailStep step, string summary, object payload, CancellationToken ct)
        => new DecisionTrailWriter(db, tenantContext, new FixedTime(at)).AppendAsync(claimId, step, TenantSeeder.HistoryActor, summary, payload, ct);

    /// <summary>The executor's recommendation summary (policy version and table row).</summary>
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

    /// <summary>A clock stopped at one instant, so a seeded trail entry carries its historical time.</summary>
    private sealed class FixedTime(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}

/// <summary>
/// A seeded claim whose remedy was just set (by the policy at <see cref="DecidedAt"/> = the approval, or by the
/// seeded-history reviewer): the claim, its product, the applicable policy version and the remedy.
/// </summary>
internal sealed record SeededRemedy(Claim Claim, Product Product, PolicyVersion Version, RemedyType Remedy, DateTimeOffset DecidedAt);

/// <summary>Runs inside a seeded claim's transaction once its remedy is set (<see cref="RemedyScenarioSeeder.SeedClaimsAsync"/>).</summary>
internal delegate Task SeededRemedyHandler(SeededRemedy remedy, CancellationToken ct);
