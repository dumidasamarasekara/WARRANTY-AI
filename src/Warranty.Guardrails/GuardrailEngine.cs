using Warranty.Domain.Adjudication;
using Warranty.Domain.Claims;
using Warranty.Guardrails.Pipeline;

namespace Warranty.Guardrails;

/// <summary>Pure, deterministic guardrail evaluation with no I/O (contracts/agents-and-tools.md).</summary>
public interface IGuardrailEngine
{
    GuardrailOutcome Evaluate(GuardrailInput input);
}

/// <summary>
/// Result of one evaluation: the ordered checks, the disposition, the escalation reasons and the
/// issued action. The harness persists it as a <see cref="GuardrailEvaluation"/>.
/// </summary>
public sealed record GuardrailOutcome(
    IReadOnlyList<GuardrailCheck> Checks,
    Disposition Disposition,
    IReadOnlyList<EscalationReason> Reasons,
    ApprovedAction? Action);

/// <summary>Runs the ordered checks and applies the disposition rules (FR-024 – FR-030).</summary>
public sealed class GuardrailEngine : IGuardrailEngine
{
    /// <summary>A further need for information after this many automatic requests escalates (FR-010, research R24).</summary>
    public const int MaxAutomaticInformationRequests = 2;

    public GuardrailOutcome Evaluate(GuardrailInput input) => throw new NotImplementedException("Implemented by T066.");
}
