using System.Text.Json.Serialization;

namespace Warranty.Domain.Fulfillment;

/// <summary>How an approved claim is fulfilled (<c>claim_remedies.remedy</c>).</summary>
public enum RemedyType
{
    Repair,
    Replacement,
    Refund,
}

/// <summary>Fulfilment scenario the remedy rules were evaluated for (<c>claim_remedies.scenario</c>).</summary>
public enum RemedyScenario
{
    CoveredDefect,
    ProtectionPlanDamage,
    RepairNotEconomical,
    RepairFailed,
}

/// <summary>Lifecycle of a claim's current remedy (<c>claim_remedies.state</c>).</summary>
public enum RemedyState
{
    AwaitingDecision,
    InProgress,
    Completed,
}

/// <summary>Why a remedy is <see cref="RemedyState.AwaitingDecision"/> (<c>claim_remedies.pending_reason</c>).</summary>
public enum RemedyPendingReason
{
    [JsonStringEnumMemberName("POLICY_CHOICE")]
    PolicyChoice,

    [JsonStringEnumMemberName("NO_REMEDY_RULE")]
    NoRemedyRule,

    [JsonStringEnumMemberName("REPAIR_FAILED_LIMIT")]
    RepairFailedLimit,

    [JsonStringEnumMemberName("REPAIR_NOT_ECONOMICAL")]
    RepairNotEconomical,
}

/// <summary><see cref="Evaluated"/> = remedy-rule evaluator output; <see cref="Decided"/> = remedy set.</summary>
public enum RemedyDecisionKind
{
    Evaluated,
    Decided,
}

/// <summary><see cref="Policy"/> = automatic single remedy; <see cref="Reviewer"/> = human choice.</summary>
public enum RemedyDecidedBy
{
    Policy,
    Reviewer,
}

/// <summary>Status of a repair case (<c>repair_cases.status</c>).</summary>
public enum RepairStatus
{
    AwaitingHandover,
    AtRepairCentre,
    ReadyForCollection,
    Collected,
    RepairNotPossible,
}

/// <summary>Set together with <see cref="RepairStatus.RepairNotPossible"/>.</summary>
public enum RepairNotPossibleReason
{
    Failed,
    NotEconomical,
}

/// <summary>Type of an append-only repair-case event.</summary>
public enum RepairEventType
{
    CentreSuggested,
    CentreChanged,
    HandedOver,
    EstimateReported,
    MarkedReady,
    Collected,
    RepairFailed,
    RepairNotEconomical,
    NewAttemptStarted,
    Transferred,
    Correction,
}

/// <summary>How a repair-case event was reported; <see cref="System"/> = recorded by the system itself.</summary>
public enum ReportSource
{
    Phone,
    Email,
    InPerson,
    Other,
    System,
}

/// <summary>How a repair centre was assigned to a handover.</summary>
public enum HandoverAssignment
{
    Suggested,
    ChosenByStaff,
    Transfer,
}

/// <summary>Outcome of a repair attempt; <see cref="Open"/> until the attempt ends.</summary>
public enum AttemptResult
{
    Open,
    Ready,
    Collected,
    Failed,
    NotEconomical,
}
