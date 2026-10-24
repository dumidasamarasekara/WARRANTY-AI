using System.Text.Json;
using System.Text.Json.Serialization;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Domain.Fulfillment;

namespace Warranty.UnitTests.Domain;

/// <summary>Pins every enum's wire names to the spellings in data-model.md.</summary>
public sealed class WireNameTests
{
    [Fact]
    public void AiDecision_uses_data_model_names()
        => WireName.All<AiDecision>().ShouldBe(
            ["APPROVE", "REJECT", "REQUEST_MORE_INFORMATION", "HUMAN_REVIEW"], ignoreOrder: true);

    [Fact]
    public void CoverageDetermination_uses_data_model_names()
        => WireName.All<CoverageDetermination>().ShouldBe(["COVERED", "NOT_COVERED", "UNDETERMINED"], ignoreOrder: true);

    [Fact]
    public void RiskSignalCode_uses_data_model_names()
        => WireName.All<RiskSignalCode>().ShouldBe(
            [
                "SOURCE_INCONSISTENCY", "PRODUCT_NOT_IN_CATALOG", "SERIAL_MISMATCH_PHOTO", "DUPLICATE_SERIAL_CLAIM",
                "EVIDENCE_REUSED", "DAMAGE_INCONSISTENT_WITH_DESCRIPTION", "PURCHASE_DATE_ANOMALY",
                "MANIPULATION_ATTEMPT", "OTHER", "PRODUCT_RETURNED_AND_REPLACED",
            ],
            ignoreOrder: true);

    [Fact]
    public void RiskSignalSource_uses_data_model_names()
        => WireName.All<RiskSignalSource>().ShouldBe(["Deterministic", "AI"], ignoreOrder: true);

    [Fact]
    public void GuardrailCheckCode_uses_data_model_names()
        => WireName.All<GuardrailCheckCode>().ShouldBe(
            [
                "SCHEMA_VALID", "REFERENCES_VALID", "REQUIRED_INFO_COMPLETE", "PRODUCT_IN_CATALOG", "POLICY_APPLICABLE",
                "COVERAGE_WINDOW_AGREES", "CLAIM_VALUE_WITHIN_LIMIT", "CONFIDENCE_AT_OR_ABOVE_MIN", "RISK_LOW",
                "NO_CONFLICTS", "NO_MANIPULATION", "CATEGORY_NOT_ALWAYS_REVIEW", "GROUNDED_IN_CLAUSE",
                "AUTO_DECISION_ENABLED", "ACTOR_AUTHORIZED", "NOT_RETURNED_FROM_REVIEW",
                "AUTO_INFO_REQUESTS_WITHIN_LIMIT", "CLAIMANT_TEXT_SAFE", "PROTECTION_PLAN_NOT_RELIED_ON",
            ],
            ignoreOrder: true);

    [Fact]
    public void EscalationReason_uses_data_model_names()
        => WireName.All<EscalationReason>().ShouldBe(
            [
                "VALUE_ABOVE_LIMIT", "CONFIDENCE_BELOW_MIN", "ALWAYS_REVIEW_CATEGORY", "RISK_MEDIUM", "RISK_HIGH",
                "EVIDENCE_CONFLICT", "AI_DETERMINISTIC_DISAGREEMENT", "INVALID_RECOMMENDATION", "AI_UNAVAILABLE",
                "AI_RECOMMENDS_REVIEW", "NO_APPLICABLE_POLICY", "AMBIGUOUS_POLICY", "PRODUCT_NOT_IN_CATALOG",
                "RETURNED_AFTER_REVIEWER_REQUEST", "INFO_INCOMPLETE_AFTER_2_REQUESTS", "UNSAFE_CLAIMANT_TEXT",
                "PROTECTION_PLAN_TO_CHECK",
            ],
            ignoreOrder: true);

    [Fact]
    public void SecurityEventKind_uses_data_model_names()
        => WireName.All<SecurityEventKind>().ShouldBe(
            [
                "ACCESS_DENIED", "CROSS_TENANT_ACCESS_DENIED", "CLAIMANT_ACCESS_FAILED", "RETRIEVAL_SCOPE_VIOLATION",
                "TOOL_SCOPE_VIOLATION", "SELF_REVIEW_REFUSED", "UNKNOWN_CHANNEL",
            ],
            ignoreOrder: true);

    [Fact]
    public void ClaimStatus_uses_data_model_names()
        => WireName.All<ClaimStatus>().ShouldBe(
            ["Submitted", "UnderEvaluation", "PendingInformation", "UnderReview", "Approved", "Rejected"],
            ignoreOrder: true);

    [Fact]
    public void TrailStep_has_the_22_data_model_steps_the_7_remedy_steps_and_the_6_replacement_steps()
        => WireName.All<TrailStep>().Count.ShouldBe(35);

    [Fact]
    public void The_replacement_trail_steps_use_data_model_names()
        => WireName.All<TrailStep>().TakeLast(6).ShouldBe(
            [
                "ReplacementLocationAssigned", "FaultyProductReturned", "ReplacementModelProposed", "ReplacementModelDecided",
                "ReplacementStatusUpdated", "ReplacementUnitRegistered",
            ]);

    [Fact]
    public void RemedyScenario_and_pending_reason_include_the_replacement_not_available_values()
    {
        WireName.All<RemedyScenario>().ShouldBe(
            ["CoveredDefect", "ProtectionPlanDamage", "RepairNotEconomical", "RepairFailed", "ReplacementNotAvailable"],
            ignoreOrder: true);
        WireName.All<RemedyPendingReason>().ShouldBe(
            ["POLICY_CHOICE", "NO_REMEDY_RULE", "REPAIR_FAILED_LIMIT", "REPAIR_NOT_ECONOMICAL", "REPLACEMENT_NOT_AVAILABLE"],
            ignoreOrder: true);
    }

    [Fact]
    public void Replacement_enums_use_data_model_names()
    {
        WireName.All<ReplacementStatus>().ShouldBe(
            ["AwaitingReturn", "AwaitingUnit", "ReadyForCollection", "HandedToCustomer", "NotAvailable", "Cancelled"],
            ignoreOrder: true);
        WireName.All<ReturnState>().ShouldBe(
            ["NotYetReturned", "Returned", "HeldByRepairCentre", "Mismatch", "Accepted"], ignoreOrder: true);
        WireName.All<ReplacementEventType>().ShouldBe(
            [
                "LocationSuggested", "LocationChanged", "ReturnRecorded", "ReturnAccepted", "ModelProposed", "ModelApproved",
                "ModelDeclined", "UnitReserved", "UnitRefused", "Moved", "HandedToCustomer", "NotAvailable", "Cancelled",
                "Correction",
            ],
            ignoreOrder: true);
        WireName.All<ReplacementProductRule>().ShouldBe(["SameModel", "SameCategoryAtLeastClaimValue"], ignoreOrder: true);
        WireName.All<ReplacementReturnRule>().ShouldBe(["AtCollection", "BeforeReservation"], ignoreOrder: true);
        WireName.All<LocationAssignment>().ShouldBe(["Suggested", "ChosenByStaff", "Moved"], ignoreOrder: true);
        WireName.All<ReplacementUnitResult>().ShouldBe(
            ["Reserved", "HandedOver", "Refused", "Released", "Cancelled"], ignoreOrder: true);
        WireName.All<ModelProposalState>().ShouldBe(["Pending", "Approved", "Declined"], ignoreOrder: true);
    }

    [Fact]
    public void Of_and_Parse_round_trip_every_value()
    {
        RoundTrip<AiDecision>();
        RoundTrip<CoverageDetermination>();
        RoundTrip<RiskSignalCode>();
        RoundTrip<RiskSignalSource>();
        RoundTrip<GuardrailCheckCode>();
        RoundTrip<EscalationReason>();
        RoundTrip<SecurityEventKind>();
        RoundTrip<ClaimStatus>();
        RoundTrip<Disposition>();
        RoundTrip<TrailStep>();
        RoundTrip<Region>();
        RoundTrip<RemedyScenario>();
        RoundTrip<RemedyPendingReason>();
        RoundTrip<ReplacementStatus>();
        RoundTrip<ReturnState>();
        RoundTrip<ReplacementEventType>();
        RoundTrip<ReplacementProductRule>();
        RoundTrip<ReplacementReturnRule>();
        RoundTrip<LocationAssignment>();
        RoundTrip<ReplacementUnitResult>();
        RoundTrip<ModelProposalState>();
    }

    [Fact]
    public void Parse_rejects_unknown_and_differently_cased_names()
    {
        WireName.TryParse<AiDecision>("approve", out _).ShouldBeFalse();
        WireName.TryParse<AiDecision>("Approve", out _).ShouldBeFalse();
        WireName.TryParse<AiDecision>(null, out _).ShouldBeFalse();
        Should.Throw<FormatException>(() => WireName.Parse<RiskSignalCode>("FRAUD"));
    }

    [Fact]
    public void System_Text_Json_uses_the_same_wire_names()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };

        JsonSerializer.Serialize(AiDecision.RequestMoreInformation, options).ShouldBe("\"REQUEST_MORE_INFORMATION\"");
        JsonSerializer.Serialize(RiskSignalSource.Ai, options).ShouldBe("\"AI\"");
        JsonSerializer.Deserialize<EscalationReason>("\"INFO_INCOMPLETE_AFTER_2_REQUESTS\"", options)
            .ShouldBe(EscalationReason.InfoIncompleteAfterTwoRequests);
    }

    [Fact]
    public void Every_escalation_reason_has_a_label_and_the_new_labels_match_the_spec()
    {
        foreach (var reason in Enum.GetValues<EscalationReason>())
        {
            reason.Label().ShouldNotBeNullOrWhiteSpace();
        }

        EscalationReason.ReturnedAfterReviewerRequest.Label().ShouldBe("returned after reviewer information request");
        EscalationReason.InfoIncompleteAfterTwoRequests.Label().ShouldBe("information still incomplete after 2 requests");
        EscalationReason.ValueAboveLimit.Label().ShouldBe("claim value above auto-approval limit");
        EscalationReason.AiUnavailable.Label().ShouldBe("AI analysis could not be completed");
        EscalationReason.ProtectionPlanToCheck.Label().ShouldBe("protection plan to be checked");
    }

    [Fact]
    public void Risk_related_reasons_are_exactly_those_hidden_from_claims_agents()
        => Enum.GetValues<EscalationReason>().Where(r => r.IsRiskRelated()).ShouldBe(
            [
                EscalationReason.RiskMedium, EscalationReason.RiskHigh, EscalationReason.EvidenceConflict,
                EscalationReason.AiDeterministicDisagreement, EscalationReason.UnsafeClaimantText,
            ],
            ignoreOrder: true);

    [Fact]
    public void Only_cross_tenant_and_unknown_channel_events_are_operator_only()
        => Enum.GetValues<SecurityEventKind>().Where(k => k.IsOperatorOnly()).ShouldBe(
            [SecurityEventKind.CrossTenantAccessDenied, SecurityEventKind.UnknownChannel],
            ignoreOrder: true);

    [Fact]
    public void Only_approved_and_rejected_are_final()
        => Enum.GetValues<ClaimStatus>().Where(s => s.IsFinal()).ShouldBe(
            [ClaimStatus.Approved, ClaimStatus.Rejected],
            ignoreOrder: true);

    private static void RoundTrip<TEnum>()
        where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
        {
            WireName.Parse<TEnum>(WireName.Of(value)).ShouldBe(value);
        }
    }
}
