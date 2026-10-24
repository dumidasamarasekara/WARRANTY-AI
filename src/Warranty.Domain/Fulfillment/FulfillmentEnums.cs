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
    ReplacementNotAvailable,
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

    [JsonStringEnumMemberName("REPLACEMENT_NOT_AVAILABLE")]
    ReplacementNotAvailable,
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

/// <summary>Where the product is when a new repair attempt starts after a failure (<c>NewAttemptStarted</c>).</summary>
public enum ProductLocation
{
    /// <summary>The product stayed at the repair centre: the new attempt starts there.</summary>
    AtCentre,

    /// <summary>The product was returned to the customer: it must be handed over again.</summary>
    ReturnedToCustomer,
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

/// <summary>Status of a replacement (<c>claims.replacements.status</c>, 003 data-model.md state machine).</summary>
public enum ReplacementStatus
{
    AwaitingReturn,
    AwaitingUnit,
    ReadyForCollection,
    HandedToCustomer,
    NotAvailable,
    Cancelled,
}

/// <summary>Where the faulty product stands (<c>claims.replacements.return_state</c>).</summary>
public enum ReturnState
{
    NotYetReturned,
    Returned,
    HeldByRepairCentre,
    Mismatch,

    /// <summary>A serial mismatch accepted by a reviewer.</summary>
    Accepted,
}

/// <summary>Type of an append-only replacement event (<c>audit.replacement_events.type</c>).</summary>
public enum ReplacementEventType
{
    LocationSuggested,
    LocationChanged,
    ReturnRecorded,
    ReturnAccepted,
    ModelProposed,
    ModelApproved,
    ModelDeclined,
    UnitReserved,
    UnitRefused,
    Moved,
    HandedToCustomer,
    NotAvailable,
    Cancelled,
    Correction,
}

/// <summary>Which product may be handed out as a replacement (<c>remedy_rules.replacement.productRule</c>).</summary>
public enum ReplacementProductRule
{
    SameModel,
    SameCategoryAtLeastClaimValue,
}

/// <summary>When the faulty product is returned (<c>remedy_rules.replacement.returnRule</c>).</summary>
public enum ReplacementReturnRule
{
    AtCollection,
    BeforeReservation,
}

/// <summary>How a replacement location was assigned (<c>claims.replacement_locations.assignment</c>).</summary>
public enum LocationAssignment
{
    Suggested,
    ChosenByStaff,
    Moved,
}

/// <summary>Outcome of a reserved replacement unit; <see cref="Released"/> = the location moved.</summary>
public enum ReplacementUnitResult
{
    Reserved,
    HandedOver,
    Refused,
    Released,
    Cancelled,
}

/// <summary>State of an equivalent-model proposal (<c>claims.replacement_model_proposals.state</c>).</summary>
public enum ModelProposalState
{
    Pending,
    Approved,
    Declined,
}