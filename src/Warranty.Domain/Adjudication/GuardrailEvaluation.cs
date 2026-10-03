using Warranty.Domain.Claims;

namespace Warranty.Domain.Adjudication;

/// <summary>One deterministic guardrail check with the values compared (FR-030).</summary>
public sealed record GuardrailCheck(
    GuardrailCheckCode Code,
    string Stage,
    bool Passed,
    string? Expected,
    string? Actual,
    string? Message);

/// <summary>The recorded result of the guardrail engine for one run: checks, disposition and reasons.</summary>
public sealed class GuardrailEvaluation
{
    private GuardrailEvaluation()
    {
        Checks = [];
        Reasons = [];
    }

    public Guid RunId { get; private set; }

    public Guid TenantId { get; private set; }

    public IReadOnlyList<GuardrailCheck> Checks { get; private set; }

    public Disposition Disposition { get; private set; }

    public IReadOnlyList<EscalationReason> Reasons { get; private set; }

    /// <summary>The issued action (type and parameters) as JSON, when the engine issued one.</summary>
    public string? ApprovedActionJson { get; private set; }

    public DateTimeOffset EvaluatedAt { get; private set; }

    public static GuardrailEvaluation Create(
        Guid runId, Guid tenantId, IEnumerable<GuardrailCheck> checks, Disposition disposition,
        IEnumerable<EscalationReason> reasons, string? approvedActionJson, DateTimeOffset evaluatedAt)
    {
        var checkList = checks.ToArray();
        var reasonList = reasons.Distinct().ToArray();
        if (checkList.Length == 0)
        {
            throw new ArgumentException("A guardrail evaluation must record its checks.", nameof(checks));
        }

        if (disposition == Disposition.HumanReview && reasonList.Length == 0)
        {
            throw new ArgumentException("An escalation must state at least one reason.", nameof(reasons));
        }

        if (disposition is Disposition.AutoApprove or Disposition.AutoReject && checkList.Any(c => !c.Passed))
        {
            throw new ArgumentException("An automatic finalization requires every check to pass.", nameof(checks));
        }

        return new GuardrailEvaluation
        {
            RunId = runId,
            TenantId = tenantId,
            Checks = checkList,
            Disposition = disposition,
            Reasons = reasonList,
            ApprovedActionJson = approvedActionJson,
            EvaluatedAt = evaluatedAt,
        };
    }
}
