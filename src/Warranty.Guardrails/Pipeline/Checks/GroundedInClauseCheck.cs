using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Domain.Policies;
using Warranty.Guardrails.Rules;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>GROUNDED_IN_CLAUSE</c>: the decision rests on a cited clause of the applicable policy version.
/// An <c>APPROVE</c> cites at least one <c>SUPPORTS_COVERAGE</c> clause (FR-026). A <c>REJECT</c> cites
/// at least one <c>SUPPORTS_REJECTION</c> clause that is either (a) a <c>Period</c> clause whose expiry
/// the deterministic coverage window confirms, or (b) an <c>Exclusion</c> clause whose
/// <c>exclusion_code</c> is listed in the version's <c>terms.exclusions</c> and evidenced by a photo
/// damage type through <see cref="ExclusionEvidenceMap"/> (FR-027, research R26). A failure prevents
/// automatic finalization; it is not an escalation condition on its own.
/// </summary>
internal sealed class GroundedInClauseCheck : IGuardrailCheck
{
    public GuardrailCheckCode Code => GuardrailCheckCode.GroundedInClause;

    public string Stage => CheckStage.Conflicts;

    public CheckResult Evaluate(GuardrailContext context)
    {
        const string expected = "decision grounded in a cited clause of the applicable policy version";
        if (context.IsShortCircuit)
        {
            return CheckResult.NotApplicable(expected, "The AI steps did not run: intake found missing items.");
        }

        if (context.ValidRecommendation is not { } recommendation)
        {
            return CheckResult.NoValidRecommendation(context, expected);
        }

        return context.Decision switch
        {
            AiDecision.Approve => EvaluateApprove(context, recommendation),
            AiDecision.Reject => EvaluateReject(context, recommendation),
            _ => CheckResult.NotApplicable(expected, "Only an APPROVE or REJECT must be grounded in a clause."),
        };
    }

    private static CheckResult EvaluateApprove(GuardrailContext context, Recommendation recommendation)
    {
        const string expected = "≥ 1 cited SUPPORTS_COVERAGE clause of the applicable version";
        var version = context.Input.Policy?.Version;
        var findings = new List<string>();
        var grounded = false;
        foreach (var citation in recommendation.PolicyRefs.Where(c => c.Relevance == PolicyRefRelevance.SupportsCoverage))
        {
            if (!context.TryGetIssuedClause(citation.Ref, out var clause))
            {
                findings.Add($"{citation.Ref}: not an issued clause");
            }
            else if (version is null || clause.PolicyVersionId != version.Id)
            {
                findings.Add($"{citation.Ref}: not a clause of the applicable version");
            }
            else
            {
                findings.Add($"{citation.Ref}: {clause.ClauseType} clause supports coverage");
                grounded = true;
            }
        }

        var actual = findings.Count == 0 ? "no SUPPORTS_COVERAGE clause cited" : Describe.List(findings);
        return grounded
            ? CheckResult.Pass(expected, actual, "The approval cites a supporting policy clause.")
            : CheckResult.BlockFinalization(
                expected, actual, "The approval cites no valid supporting policy clause.", EscalationReason.InvalidRecommendation);
    }

    private static CheckResult EvaluateReject(GuardrailContext context, Recommendation recommendation)
    {
        const string expected =
            "≥ 1 cited SUPPORTS_REJECTION clause: a Period clause confirmed by the coverage window, or a listed Exclusion evidenced by a photo";
        var version = context.Input.Policy?.Version;
        var photos = context.Input.Evidence?.Photos ?? [];
        var findings = new List<string>();
        var grounded = false;
        foreach (var citation in recommendation.PolicyRefs.Where(c => c.Relevance == PolicyRefRelevance.SupportsRejection))
        {
            var (isGrounded, finding) = Ground(context, citation.Ref, version, photos);
            findings.Add(finding);
            grounded |= isGrounded;
        }

        var actual = findings.Count == 0 ? "no SUPPORTS_REJECTION clause cited" : Describe.List(findings);
        return grounded
            ? CheckResult.Pass(expected, actual, "The rejection is grounded in a confirmed clause.")
            : CheckResult.BlockFinalization(
                expected,
                actual,
                "The rejection is not grounded in a clause confirmed by the independent checks.",
                EscalationReason.AiDeterministicDisagreement);
    }

    private static (bool Grounded, string Finding) Ground(
        GuardrailContext context, string reference, PolicyVersion? version, IReadOnlyList<PhotoFinding> photos)
    {
        if (!context.TryGetIssuedClause(reference, out var clause))
        {
            return (false, $"{reference}: not an issued clause");
        }

        if (version is null || clause.PolicyVersionId != version.Id)
        {
            return (false, $"{reference}: not a clause of the applicable version");
        }

        switch (clause.ClauseType)
        {
            case ClauseType.Period:
                return context.WithinCoverage == false
                    ? (true, $"{reference}: Period clause, coverage ended before the claim date")
                    : (false, $"{reference}: Period clause, but expiry is not confirmed by the coverage window");

            case ClauseType.Exclusion when clause.ExclusionCode is { } code:
                var name = Describe.Wire(code);
                if (!version.Terms.Excludes(code))
                {
                    return (false, $"{reference}: Exclusion {name} is not listed in the version's terms");
                }

                var photo = photos.FirstOrDefault(p => p.DamageTypes is not null && ExclusionEvidenceMap.IsSupported(code, p.DamageTypes));
                return photo is null
                    ? (false, $"{reference}: Exclusion {name} is not evidenced by any photo damage type")
                    : (true, $"{reference}: Exclusion {name} evidenced by {photo.EvidenceRef} ({Describe.List(photo.DamageTypes)})");

            case ClauseType.Exclusion:
                return (false, $"{reference}: Exclusion clause without an exclusion code");

            default:
                return (false, $"{reference}: a {clause.ClauseType} clause cannot ground a rejection");
        }
    }
}
