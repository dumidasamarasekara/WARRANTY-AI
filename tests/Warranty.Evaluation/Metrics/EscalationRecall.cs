namespace Warranty.Evaluation.Metrics;

public sealed record EscalationRecallResult(
    Ratio Recall,
    IReadOnlyList<string> AutoFinalizedCaseIds,
    IReadOnlyList<string> NotEscalatedCaseIds,
    Ratio ReasonRecall,
    IReadOnlyList<string> MissingReasons);

/// <summary>
/// Escalation recall (SC-004, must be 100%): of the cases labelled <c>HumanReview</c>, the share the
/// guardrails routed to human review. A should-escalate case that was finalized automatically is the
/// critical failure and is listed separately. Reason recall checks that each labelled escalation reason is
/// among the run's reasons (other reasons may also appear).
/// </summary>
public static class EscalationRecall
{
    public const string HumanReview = "HumanReview";

    private static readonly HashSet<string> AutoFinal = new(StringComparer.Ordinal) { "AutoApprove", "AutoReject" };

    public static EscalationRecallResult Compute(IEnumerable<ScoredCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var shouldEscalate = cases.Where(c => c.Expected.Disposition == HumanReview).ToList();

        var recall = Ratio.Empty;
        var reasons = Ratio.Empty;
        var missing = new List<string>();
        foreach (var c in shouldEscalate)
        {
            recall = recall.Add(c.Observation.Disposition == HumanReview);
            foreach (var reason in c.Expected.EscalationReasons)
            {
                var found = c.Observation.EscalationReasons.Contains(reason, StringComparer.Ordinal);
                reasons = reasons.Add(found);
                if (!found)
                {
                    missing.Add($"{c.CaseId}: {reason}");
                }
            }
        }

        return new EscalationRecallResult(
            recall,
            shouldEscalate.Where(c => c.Observation.Disposition is { } d && AutoFinal.Contains(d)).Select(c => c.CaseId).ToList(),
            shouldEscalate.Where(c => c.Observation.Disposition != HumanReview).Select(c => c.CaseId).ToList(),
            reasons,
            missing);
    }
}
