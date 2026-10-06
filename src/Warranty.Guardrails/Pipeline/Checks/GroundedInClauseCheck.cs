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
/// damage type through <see cref="ExclusionEvidenceMap"/> (FR-027, research R26), or (c) a <c>Coverage</c>
/// clause when a photo shows accidental damage and <see cref="AccidentalDamageRule"/> confirms it is beyond
/// the version's allowance. An <c>APPROVE</c> of a claim whose photos show accidental damage additionally
/// needs the rule to confirm the allowance covers it (T077). A failure prevents automatic finalization; it
/// is not an escalation condition on its own.
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

        if (findings.Count == 0)
        {
            findings.Add("no SUPPORTS_COVERAGE clause cited");
        }

        if (!grounded)
        {
            return CheckResult.BlockFinalization(
                expected, Describe.List(findings), "The approval cites no valid supporting policy clause.", EscalationReason.InvalidRecommendation);
        }

        // Accidental damage shown by a photo is covered only within the version's allowance (T077).
        if (context.AccidentalDamageEvidence is { } photo)
        {
            var allowance = context.AccidentalDamage;
            findings.Add($"{photo.EvidenceRef} shows accidental damage ({Describe.List(photo.DamageTypes)}): {DescribeAllowance(allowance)}");
            if (!allowance.IsCovered)
            {
                return CheckResult.BlockFinalization(
                    expected,
                    Describe.List(findings),
                    "The approval covers accidental damage that the policy's accidental-damage allowance does not cover.",
                    EscalationReason.AiDeterministicDisagreement);
            }
        }

        return CheckResult.Pass(expected, Describe.List(findings), "The approval cites a supporting policy clause.");
    }

    /// <summary>A staff-facing description of the accidental-damage allowance decision.</summary>
    internal static string DescribeAllowance(AccidentalDamageResult allowance) => allowance.Outcome switch
    {
        AccidentalDamageOutcome.Covered =>
            $"within the accidental-damage allowance (until {Describe.Date(allowance.WindowEndDate!.Value)}, "
            + $"{Describe.Number(allowance.PriorApprovedIncidents)} of {Describe.Number(allowance.MaxIncidents)} incident(s) used)",
        AccidentalDamageOutcome.NotCovered => "the applicable version does not cover accidental damage",
        AccidentalDamageOutcome.OutsideWindow =>
            $"the accidental-damage allowance ended on {Describe.Date(allowance.WindowEndDate!.Value)}",
        AccidentalDamageOutcome.IncidentLimitReached =>
            $"the accidental-damage allowance is used up ({Describe.Number(allowance.PriorApprovedIncidents)} of "
            + $"{Describe.Number(allowance.MaxIncidents)} incident(s) already approved)",
        _ => "the accidental-damage allowance is not determined: no applicable policy version",
    };

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

            // An accidental-damage allowance is coverage, not an exclusion (research R26): its Coverage clause
            // grounds a rejection only when a photo shows accidental damage and the rule confirms it is beyond the allowance.
            case ClauseType.Coverage when context.AccidentalDamageEvidence is { } accidental:
                var allowance = context.AccidentalDamage;
                var evidence = $"{accidental.EvidenceRef} shows accidental damage ({Describe.List(accidental.DamageTypes)})";
                return allowance.IsBeyondAllowance
                    ? (true, $"{reference}: Coverage clause, {evidence} and {DescribeAllowance(allowance)}")
                    : (false, $"{reference}: Coverage clause, {evidence}, but the rejection is not confirmed: {DescribeAllowance(allowance)}");

            default:
                return (false, $"{reference}: a {clause.ClauseType} clause cannot ground a rejection");
        }
    }
}
