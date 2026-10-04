using Warranty.Domain.Claims;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>AUTO_INFO_REQUESTS_WITHIN_LIMIT</c>: fails when information is still needed and
/// <see cref="GuardrailEngine.MaxAutomaticInformationRequests"/> automatic requests were already made
/// (FR-010, research R24). Evaluated on every path.
/// </summary>
internal sealed class AutoInfoRequestsWithinLimitCheck : IGuardrailCheck
{
    public GuardrailCheckCode Code => GuardrailCheckCode.AutoInfoRequestsWithinLimit;

    public string Stage => CheckStage.LoopState;

    public CheckResult Evaluate(GuardrailContext context)
    {
        const int limit = GuardrailEngine.MaxAutomaticInformationRequests;
        var count = context.Input.Case.AutoInfoRequestCount;
        var expected = $"auto_info_request_count < {Describe.Number(limit)} when information is needed";
        var actual = $"auto_info_request_count = {Describe.Number(count)}; information needed: {Describe.Bool(context.InformationNeeded)}";

        if (!context.InformationNeeded)
        {
            return CheckResult.Pass(expected, actual, "No request for information is needed.");
        }

        return count < limit
            ? CheckResult.Pass(expected, actual, "Another automatic request for information is allowed.")
            : CheckResult.Escalate(
                expected,
                actual,
                "Information is still incomplete after the maximum number of automatic requests.",
                EscalationReason.InfoIncompleteAfterTwoRequests);
    }
}
