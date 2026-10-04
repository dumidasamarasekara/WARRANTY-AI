using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Guardrails.Pipeline;
using Warranty.Guardrails.Pipeline.Checks;

namespace Warranty.Guardrails;

/// <summary>Pure, deterministic guardrail evaluation with no I/O (contracts/agents-and-tools.md).</summary>
public interface IGuardrailEngine
{
    GuardrailOutcome Evaluate(GuardrailInput input);
}

/// <summary>
/// Result of one evaluation: the ordered checks, the disposition, the escalation reasons and the
/// issued action. The harness persists it as a <see cref="GuardrailEvaluation"/>; <see cref="Reasons"/>
/// is non-empty exactly when <see cref="Disposition"/> is <see cref="Disposition.HumanReview"/>, and an
/// automatic finalization has every check passed, so <see cref="GuardrailEvaluation.Create"/> accepts it.
/// </summary>
public sealed record GuardrailOutcome(
    IReadOnlyList<GuardrailCheck> Checks,
    Disposition Disposition,
    IReadOnlyList<EscalationReason> Reasons,
    ApprovedAction? Action);

/// <summary>
/// Runs the ordered checks and applies the disposition rules (FR-024 – FR-030, contracts/agents-and-tools.md
/// disposition table). Every evaluation records all checks in data-model.md order and issues exactly one
/// <see cref="ApprovedAction"/>:
/// <list type="number">
/// <item>Any escalation condition (an escalating check failed, or the AI recommends <c>HUMAN_REVIEW</c>) →
/// <see cref="Disposition.HumanReview"/> (<see cref="ActionKind.EscalateToReview"/>).</item>
/// <item>Otherwise, information is needed (missing items, or the AI recommends
/// <c>REQUEST_MORE_INFORMATION</c>) → <see cref="Disposition.RequestInformation"/> with the items
/// (FR-010, FR-029). Returned-from-review and the request limit are escalating checks, so a would-be
/// request becomes <see cref="Disposition.HumanReview"/> (research R24).</item>
/// <item>Otherwise, AI <c>APPROVE</c>/<c>REJECT</c> with every check passed →
/// <see cref="Disposition.AutoApprove"/>/<see cref="Disposition.AutoReject"/>.</item>
/// <item>Anything else → <see cref="Disposition.HumanReview"/>.</item>
/// </list>
/// </summary>
public sealed class GuardrailEngine : IGuardrailEngine
{
    /// <summary>A further need for information after this many automatic requests escalates (FR-010, research R24).</summary>
    public const int MaxAutomaticInformationRequests = 2;

    /// <summary>The checks in data-model.md order (the order of <see cref="GuardrailCheckCode"/>).</summary>
    private static readonly IReadOnlyList<IGuardrailCheck> OrderedChecks =
    [
        new SchemaValidCheck(),
        new ReferencesValidCheck(),
        new RequiredInfoCompleteCheck(),
        new ProductInCatalogCheck(),
        new PolicyApplicableCheck(),
        new CoverageWindowAgreesCheck(),
        new ClaimValueWithinLimitCheck(),
        new ConfidenceAtOrAboveMinCheck(),
        new RiskLowCheck(),
        new NoConflictsCheck(),
        new NoManipulationCheck(),
        new CategoryNotAlwaysReviewCheck(),
        new GroundedInClauseCheck(),
        new AutoDecisionEnabledCheck(),
        new ActorAuthorizedCheck(),
        new NotReturnedFromReviewCheck(),
        new AutoInfoRequestsWithinLimitCheck(),
        new ClaimantTextSafeCheck(),
    ];

    /// <summary>Evaluates one run.</summary>
    /// <exception cref="ArgumentException">
    /// An input belongs to another tenant or run than <see cref="CaseFacts"/> (a caller defect); no action is issued.
    /// </exception>
    public GuardrailOutcome Evaluate(GuardrailInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        VerifyInputIntegrity(input);

        var context = new GuardrailContext(input);
        var results = OrderedChecks.Select(check => (Check: check, Result: check.Evaluate(context))).ToArray();
        var checks = results
            .Select(r => new GuardrailCheck(r.Check.Code, r.Check.Stage, r.Result.Passed, r.Result.Expected, r.Result.Actual, r.Result.Message))
            .ToArray();

        var aiRecommendsReview = context.Decision == AiDecision.HumanReview;
        var escalate = aiRecommendsReview || results.Any(r => !r.Result.Passed && r.Result.Escalates);
        var allPassed = results.All(r => r.Result.Passed);

        Disposition disposition;
        if (escalate)
        {
            disposition = Disposition.HumanReview;
        }
        else if (context.InformationNeeded)
        {
            disposition = Disposition.RequestInformation;
        }
        else if (allPassed && context.Decision == AiDecision.Approve)
        {
            disposition = Disposition.AutoApprove;
        }
        else if (allPassed && context.Decision == AiDecision.Reject)
        {
            disposition = Disposition.AutoReject;
        }
        else
        {
            disposition = Disposition.HumanReview;
        }

        var reasons = disposition == Disposition.HumanReview
            ? CollectReasons(aiRecommendsReview, results.Select(r => r.Result))
            : [];
        if (disposition == Disposition.HumanReview && reasons.Count == 0)
        {
            throw new InvalidOperationException("Guardrail defect: an escalation without a reason.");
        }

        if (disposition == Disposition.RequestInformation && context.RequestedItems.Count == 0)
        {
            throw new InvalidOperationException("Guardrail defect: a request for information without items.");
        }

        return new GuardrailOutcome(checks, disposition, reasons, IssueAction(input.Case, disposition, context.RequestedItems));
    }

    private static List<EscalationReason> CollectReasons(bool aiRecommendsReview, IEnumerable<CheckResult> results)
    {
        var reasons = new List<EscalationReason>();
        if (aiRecommendsReview)
        {
            reasons.Add(EscalationReason.AiRecommendsReview);
        }

        foreach (var result in results)
        {
            if (!result.Passed && result.Reason is { } reason && !reasons.Contains(reason))
            {
                reasons.Add(reason);
            }
        }

        return reasons;
    }

    private static ApprovedAction IssueAction(CaseFacts facts, Disposition disposition, IReadOnlyList<RequestedItem> requestedItems)
    {
        var kind = disposition switch
        {
            Disposition.AutoApprove => ActionKind.FinalizeApproved,
            Disposition.AutoReject => ActionKind.FinalizeRejected,
            Disposition.RequestInformation => ActionKind.RequestInformation,
            _ => ActionKind.EscalateToReview,
        };

        return new ApprovedAction(
            kind, facts.TenantId, facts.ClaimId, facts.RunId, kind == ActionKind.RequestInformation ? requestedItems : []);
    }

    /// <summary>
    /// Every input must belong to the claim's tenant and run: tenant identity comes only from the job
    /// record (via <see cref="CaseFacts"/>), never from model output.
    /// </summary>
    private static void VerifyInputIntegrity(GuardrailInput input)
    {
        var facts = input.Case ?? throw new ArgumentException("Case facts are required.", nameof(input));
        if (facts.TenantId == Guid.Empty || facts.ClaimId == Guid.Empty || facts.RunId == Guid.Empty)
        {
            throw new ArgumentException("Case facts must identify the tenant, claim and run.", nameof(input));
        }

        Require(input.Settings is not null && input.Settings.TenantId == facts.TenantId, "tenant settings");
        Require(input.Intake is not null && input.Intake.TenantId == facts.TenantId && input.Intake.RunId == facts.RunId, "intake result");
        Require(input.Risk is null || (input.Risk.TenantId == facts.TenantId && input.Risk.RunId == facts.RunId), "risk assessment");
        Require(
            input.Recommendation is null
            || (input.Recommendation.TenantId == facts.TenantId && input.Recommendation.RunId == facts.RunId),
            "recommendation");
        Require(input.Policy?.Version is null || input.Policy.Version.TenantId == facts.TenantId, "policy version");
        Require(
            input.Policy is null || input.Policy.Clauses.All(c => c.TenantId == facts.TenantId && c.RunId == facts.RunId),
            "retrieved policy clauses");
        Require(input.IssuedReferences is not null, "issued references");
        Require(input.Actor is not null, "actor");

        static void Require(bool condition, string what)
        {
            if (!condition)
            {
                throw new ArgumentException($"The {what} must be present and belong to the claim's tenant and run.", nameof(input));
            }
        }
    }
}
