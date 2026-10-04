using Warranty.Domain.Claims;
using Warranty.Guardrails.Rules;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>CLAIMANT_TEXT_SAFE</c>: the AI's claimant explanation contains no risk/fraud vocabulary, signal
/// codes or internal reference IDs (<see cref="ClaimantTextScreen"/>, research R25). An automatic decision
/// needs a non-empty explanation. Fails closed: a screening error counts as unsafe.
/// </summary>
internal sealed class ClaimantTextSafeCheck : IGuardrailCheck
{
    private const string Expected = "no risk/fraud terms, signal codes or reference IDs";

    public GuardrailCheckCode Code => GuardrailCheckCode.ClaimantTextSafe;

    public string Stage => CheckStage.ClaimantText;

    public CheckResult Evaluate(GuardrailContext context)
    {
        if (context.IsShortCircuit)
        {
            return CheckResult.NotApplicable(Expected, "The AI steps did not run: there is no AI claimant text.");
        }

        if (context.ValidRecommendation is not { } recommendation)
        {
            return CheckResult.NoValidRecommendation(context, Expected);
        }

        var text = recommendation.ClaimantExplanation;
        if (string.IsNullOrWhiteSpace(text))
        {
            return context.Decision is AiDecision.Approve or AiDecision.Reject
                ? Unsafe("empty", "An automatic decision needs a claimant explanation.")
                : CheckResult.Pass(Expected, "empty", "There is no claimant text to screen.");
        }

        ClaimantTextScreenResult result;
        try
        {
            result = ClaimantTextScreen.Screen(text);
        }
#pragma warning disable CA1031 // Fail closed: any screening error makes the text unsafe.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return Unsafe($"screening failed ({exception.GetType().Name})", "The claimant text could not be screened.");
        }

        return result.IsSafe
            ? CheckResult.Pass(Expected, "safe", "The claimant text is safe to show.")
            : Unsafe($"contains '{result.OffendingTerm}'", "The claimant text contains a term that must not be shown to claimants.");
    }

    private static CheckResult Unsafe(string actual, string message)
        => CheckResult.Escalate(Expected, actual, message, EscalationReason.UnsafeClaimantText);
}
