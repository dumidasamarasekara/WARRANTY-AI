using System.Text.Json.Serialization;

namespace Warranty.Domain.Adjudication;

public enum RunStatus
{
    Running,
    Completed,
    Failed,
}

/// <summary>Execution-state checkpoint of a run; steps only move forward (resume skips completed steps).</summary>
public enum RunStep
{
    Intake,
    Evidence,
    Policy,
    Risk,
    Decision,
    Guardrails,
    Action,
    Done,
}

/// <summary>Result of deterministic policy-version selection for the purchase date (clarification Q1).</summary>
public enum PolicyVersionOutcome
{
    Ok,
    NoApplicablePolicy,
    AmbiguousPolicyVersion,
}

/// <summary><see cref="Intake"/> = signals available before evidence analysis (intake short-circuit).</summary>
public enum RiskAssessmentStage
{
    Intake,
    Full,
}

public enum EvidenceFindingKind
{
    InvoiceExtraction,
    PhotoAnalysis,
}

/// <summary>How a cited policy reference relates to the recommendation (decision schema <c>policyRefs.relevance</c>).</summary>
public enum PolicyRefRelevance
{
    [JsonStringEnumMemberName("SUPPORTS_COVERAGE")]
    SupportsCoverage,

    [JsonStringEnumMemberName("SUPPORTS_REJECTION")]
    SupportsRejection,

    [JsonStringEnumMemberName("DEFINES_PERIOD")]
    DefinesPeriod,

    [JsonStringEnumMemberName("CONTEXT")]
    Context,
}
