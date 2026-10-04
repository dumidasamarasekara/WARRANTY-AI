using Warranty.Domain.Claims;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>REFERENCES_VALID</c>: every cited evidence reference is an <c>EV-n</c> and every cited policy
/// reference a retrieved <c>POL-n</c> issued for this run (FR-022); an <c>APPROVE</c>/<c>REJECT</c>
/// cites at least one evidence item (SC-006). A violation makes the recommendation invalid.
/// </summary>
internal sealed class ReferencesValidCheck : IGuardrailCheck
{
    private const string Expected = "every cited EV-n and POL-n issued for this run";

    public GuardrailCheckCode Code => GuardrailCheckCode.ReferencesValid;

    public string Stage => CheckStage.References;

    public CheckResult Evaluate(GuardrailContext context)
    {
        if (context.IsShortCircuit)
        {
            return CheckResult.NotApplicable(Expected, "The AI steps did not run: intake found missing items.");
        }

        if (context.ValidRecommendation is not { } recommendation)
        {
            return CheckResult.NoValidRecommendation(context, Expected);
        }

        var invalid = recommendation.EvidenceRefs
            .Select(citation => citation.Ref)
            .Where(reference => !context.IsIssuedEvidence(reference))
            .Concat(recommendation.PolicyRefs
                .Select(citation => citation.Ref)
                .Where(reference => !context.TryGetIssuedClause(reference, out _)))
            .Select(reference => reference ?? "(null)")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (invalid.Length > 0)
        {
            return CheckResult.Escalate(
                Expected,
                $"not issued for this run: {Describe.List(invalid)}",
                "The recommendation cites references that were not issued for this claim.",
                EscalationReason.InvalidRecommendation);
        }

        if (context.Decision is AiDecision.Approve or AiDecision.Reject && recommendation.EvidenceRefs.Count == 0)
        {
            return CheckResult.Escalate(
                Expected,
                "no evidence reference cited",
                "A decision must cite the evidence it relies on.",
                EscalationReason.InvalidRecommendation);
        }

        var cited = recommendation.EvidenceRefs.Select(citation => citation.Ref)
            .Concat(recommendation.PolicyRefs.Select(citation => citation.Ref))
            .Distinct(StringComparer.Ordinal);
        return CheckResult.Pass(Expected, $"cited: {Describe.List(cited)}", "Every cited reference was issued for this run.");
    }
}
