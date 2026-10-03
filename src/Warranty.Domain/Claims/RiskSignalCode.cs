using System.Text.Json.Serialization;

namespace Warranty.Domain.Claims;

/// <summary>Risk signal codes (FR-017, data-model.md risk_assessments).</summary>
public enum RiskSignalCode
{
    /// <summary>Serial, model, price or seller differ between sources (a date mismatch is <see cref="PurchaseDateAnomaly"/>).</summary>
    [JsonStringEnumMemberName("SOURCE_INCONSISTENCY")]
    SourceInconsistency,

    [JsonStringEnumMemberName("PRODUCT_NOT_IN_CATALOG")]
    ProductNotInCatalog,

    [JsonStringEnumMemberName("SERIAL_MISMATCH_PHOTO")]
    SerialMismatchPhoto,

    /// <summary>Another claim for the same serial that is open or was finalized in the last 90 days (research R25).</summary>
    [JsonStringEnumMemberName("DUPLICATE_SERIAL_CLAIM")]
    DuplicateSerialClaim,

    [JsonStringEnumMemberName("EVIDENCE_REUSED")]
    EvidenceReused,

    [JsonStringEnumMemberName("DAMAGE_INCONSISTENT_WITH_DESCRIPTION")]
    DamageInconsistentWithDescription,

    [JsonStringEnumMemberName("PURCHASE_DATE_ANOMALY")]
    PurchaseDateAnomaly,

    [JsonStringEnumMemberName("MANIPULATION_ATTEMPT")]
    ManipulationAttempt,

    /// <summary>AI-reported signal outside the fixed set.</summary>
    [JsonStringEnumMemberName("OTHER")]
    Other,
}
