using Warranty.Domain.Claims;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>NOT_RETURNED_FROM_REVIEW</c>: fails once a reviewer has requested more information on the claim;
/// every later round ends in human review whatever the AI recommends (FR-028, FR-034, research R24).
/// Evaluated on every path.
/// </summary>
internal sealed class NotReturnedFromReviewCheck : IGuardrailCheck
{
    private const string Expected = "reviewer_info_requested = false";

    public GuardrailCheckCode Code => GuardrailCheckCode.NotReturnedFromReview;

    public string Stage => CheckStage.LoopState;

    public CheckResult Evaluate(GuardrailContext context)
        => context.Input.Case.ReviewerInfoRequested
            ? CheckResult.Escalate(
                Expected,
                "reviewer_info_requested = true",
                "The claim returned after a reviewer's information request.",
                EscalationReason.ReturnedAfterReviewerRequest)
            : CheckResult.Pass(Expected, "reviewer_info_requested = false", "No reviewer has requested more information.");
}
