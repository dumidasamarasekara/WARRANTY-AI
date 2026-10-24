using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Domain.Policies;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>
/// <c>PROTECTION_PLAN_NOT_RELIED_ON</c> (002 FR-006, FR-037, research R9): a claim that relies on a stated
/// protection plan to lift an accidental-damage limit is never decided automatically. It fails — and escalates
/// with <see cref="EscalationReason.ProtectionPlanToCheck"/> — only when all of these hold: the claim states an
/// additional protection plan; the damage is accidental (a photo shows it, or the AI cites an
/// <c>ACCIDENTAL_DAMAGE</c> exclusion clause as ground for rejection); the applicable version excludes accidental
/// damage, or the damage is beyond its accidental-damage allowance (<see cref="Rules.AccidentalDamageRule"/>);
/// and the version's <c>remedies.protectionPlan.liftsAccidentalDamageExclusion</c> is true. In every other case
/// the plan changes nothing and the check passes, so claims without a plan keep their 001 outcome (SC-009).
/// Plan validity is a document check left to a reviewer. Not evaluated when intake short-circuits: the missing
/// items (e.g. the plan document) are requested first.
/// </summary>
internal sealed class ProtectionPlanNotReliedOnCheck : IGuardrailCheck
{
    private const string Expected = "no automatic decision that relies on a protection plan lifting the accidental-damage limits";

    public GuardrailCheckCode Code => GuardrailCheckCode.ProtectionPlanNotReliedOn;

    public string Stage => CheckStage.BusinessRules;

    public CheckResult Evaluate(GuardrailContext context)
    {
        if (context.IsShortCircuit)
        {
            return CheckResult.NotApplicable(Expected, "The AI steps did not run: intake found missing items.");
        }

        if (!context.Input.Case.HasProtectionPlan)
        {
            return CheckResult.Pass(Expected, "no protection plan stated", "The claim states no protection plan.");
        }

        if (context.Input.Policy?.Version is not { } version)
        {
            return CheckResult.Pass(
                Expected, "protection plan stated; no applicable policy version", "Without an applicable policy version a plan lifts nothing.");
        }

        if (version.RemedyRules?.ProtectionPlan is not { LiftsAccidentalDamageExclusion: true })
        {
            return CheckResult.Pass(
                Expected,
                "protection plan stated; the version does not let a plan lift the accidental-damage exclusion",
                "The applicable version does not let a protection plan lift the accidental-damage exclusion, so the plan changes nothing.");
        }

        if (AccidentalDamageShownBy(context) is not { } evidence)
        {
            return CheckResult.Pass(
                Expected, "protection plan stated; no accidental damage", "The claim does not rest on accidental damage, so the plan changes nothing.");
        }

        var allowance = context.AccidentalDamage;
        var limit = version.Terms.Excludes(ExclusionCode.AccidentalDamage)
            ? "the version excludes accidental damage"
            : allowance.IsBeyondAllowance
                ? GroundedInClauseCheck.DescribeAllowance(allowance)
                : null;
        var actual = $"protection plan stated; {evidence}; {limit ?? GroundedInClauseCheck.DescribeAllowance(allowance)}";
        return limit is null
            ? CheckResult.Pass(Expected, actual, "The accidental damage is covered without the plan.")
            : CheckResult.Escalate(
                Expected,
                actual,
                "The claim relies on the protection plan to lift the accidental-damage limits; a reviewer must check the plan.",
                EscalationReason.ProtectionPlanToCheck);
    }

    /// <summary>What shows the damage is accidental: a photo (research R26 mapping) or a cited accidental-damage exclusion; null when nothing does.</summary>
    private static string? AccidentalDamageShownBy(GuardrailContext context)
    {
        if (context.AccidentalDamageEvidence is { } photo)
        {
            return $"{photo.EvidenceRef} shows accidental damage ({Describe.List(photo.DamageTypes)})";
        }

        var cited = context.ValidRecommendation?.PolicyRefs.FirstOrDefault(citation =>
            citation.Relevance == PolicyRefRelevance.SupportsRejection
            && context.TryGetIssuedClause(citation.Ref, out var clause)
            && clause.ClauseType == ClauseType.Exclusion
            && clause.ExclusionCode == ExclusionCode.AccidentalDamage);
        return cited is null ? null : $"{cited.Ref} (accidental-damage exclusion) is cited as ground for rejection";
    }
}
