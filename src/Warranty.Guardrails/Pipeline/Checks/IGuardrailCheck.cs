using System.Globalization;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.Guardrails.Pipeline.Checks;

/// <summary>One deterministic guardrail check (data-model.md check codes); pure, no I/O.</summary>
internal interface IGuardrailCheck
{
    GuardrailCheckCode Code { get; }

    /// <summary>The engine stage the check belongs to (research R13), see <see cref="CheckStage"/>.</summary>
    string Stage { get; }

    CheckResult Evaluate(GuardrailContext context);
}

/// <summary>Engine stages, in the order of research R13.</summary>
internal static class CheckStage
{
    public const string Schema = "Schema";
    public const string References = "References";
    public const string BusinessRules = "BusinessRules";
    public const string Thresholds = "Thresholds";
    public const string Conflicts = "Conflicts";
    public const string Authorization = "Authorization";
    public const string LoopState = "LoopState";
    public const string ClaimantText = "ClaimantText";
}

/// <summary>
/// Outcome of one check: pass/fail with the values compared (FR-030), the escalation reason a failure
/// contributes and whether the failure alone forces <see cref="Disposition.HumanReview"/>. A failure
/// that does not escalate still prevents automatic finalization.
/// </summary>
internal sealed record CheckResult(
    bool Passed,
    string Expected,
    string Actual,
    string Message,
    EscalationReason? Reason,
    bool Escalates)
{
    public const string NotApplicableValue = "not applicable";

    public static CheckResult Pass(string expected, string actual, string message)
        => new(true, expected, actual, message, null, false);

    /// <summary>The check does not apply to this path or decision; it can never enable an automatic finalization on its own.</summary>
    public static CheckResult NotApplicable(string expected, string message)
        => new(true, expected, NotApplicableValue, message, null, false);

    /// <summary>A failure that is an FR-028 escalation condition: the disposition is <see cref="Disposition.HumanReview"/>.</summary>
    public static CheckResult Escalate(string expected, string actual, string message, EscalationReason reason)
        => new(false, expected, actual, message, reason, true);

    /// <summary>A failure that only prevents automatic finalization (it does not block a request for information).</summary>
    public static CheckResult BlockFinalization(string expected, string actual, string message, EscalationReason reason)
        => new(false, expected, actual, message, reason, false);

    /// <summary>Information is missing and can be requested from the submitter (FR-010); not an escalation.</summary>
    public static CheckResult InformationMissing(string expected, string actual, string message)
        => new(false, expected, actual, message, null, false);

    /// <summary>Failure of a check that needs a valid recommendation when there is none (FR-023, FR-031).</summary>
    public static CheckResult NoValidRecommendation(GuardrailContext context, string expected)
        => Escalate(
            expected,
            context.Input.Recommendation is null ? "no recommendation" : "invalid recommendation",
            "Not satisfied: there is no valid AI recommendation to check.",
            context.MissingRecommendationReason);
}

/// <summary>Invariant formatting of the values recorded in checks.</summary>
internal static class Describe
{
    public static string Wire<TEnum>(TEnum value)
        where TEnum : struct, Enum
        => WireName.Of(value);

    public static string Money(decimal value, string currency)
        => string.Create(CultureInfo.InvariantCulture, $"{value:0.00} {currency}");

    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Bool(bool value) => value ? "true" : "false";

    public static string List(IEnumerable<string> values)
    {
        var joined = string.Join(", ", values);
        return joined.Length == 0 ? "none" : joined;
    }
}
