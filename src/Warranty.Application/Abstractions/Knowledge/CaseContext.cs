using System.Text.Json.Serialization;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;
using Warranty.Guardrails.Pipeline;

namespace Warranty.Application.Abstractions.Knowledge;

/// <summary>
/// Everything the harness knows about one claim round. The customer appears only as a redacted view:
/// identifiers are placeholders and never reach a prompt (FR-006a, research R28).
/// <see cref="HasProtectionPlan"/> is the claim's stated additional protection plan (002 FR-036).
/// <see cref="ReplacementCover"/> is set when the claimed serial is a registered replacement unit (003 research R11).
/// </summary>
public sealed record CaseContext(
    Guid ClaimId,
    int Round,
    string Reference,
    ClaimChannel Channel,
    DateOnly ClaimDate,
    DateOnly PurchaseDate,
    string PurchasePlace,
    decimal PurchasePrice,
    string Currency,
    Region? Region,
    string ProductModelCode,
    string SerialNumber,
    string ProblemDescription,
    CaseProduct? Product,
    CaseCustomerView Customer,
    IReadOnlyList<CaseEvidence> Evidence,
    ClaimHistoryCounts History,
    bool ReviewerInfoRequested,
    int AutoInfoRequestCount,
    bool HasProtectionPlan = false,
    ReplacementCover? ReplacementCover = null);

/// <summary>The catalog product, when the product/serial pair is in the tenant's catalog.</summary>
public sealed record CaseProduct(Guid ProductId, string ModelCode, string Name, string Category, decimal ClaimValue);

/// <summary>Redacted customer view: only placeholders plus country and region.</summary>
public sealed record CaseCustomerView(string Country, Region? Region)
{
    public const string NamePlaceholder = "[CUSTOMER]";
    public const string EmailPlaceholder = "[EMAIL]";
    public const string PhonePlaceholder = "[PHONE]";
    public const string AddressPlaceholder = "[ADDRESS]";
}

/// <summary>One evidence file of the claim (all rounds up to the current one).</summary>
public sealed record CaseEvidence(
    Guid EvidenceId,
    EvidenceKind Kind,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    int Round,
    string BlobPath);

/// <summary>
/// Same-tenant claim history counts for the serial (research R25): duplicates are other claims that
/// are open or were finalized within 90 days; accidental-damage approvals count all-time.
/// <see cref="ReturnedAndReplaced"/> is true when the serial was returned as faulty and replaced (003 FR-030); it
/// reaches the run as the deterministic <c>PRODUCT_RETURNED_AND_REPLACED</c> signal and is not serialized, so the
/// <c>claim_history_lookup</c> output the models see stays counts only (003 research R11: no prompt change).
/// </summary>
public sealed record ClaimHistoryCounts(
    int DuplicateClaimsForSerial,
    int PriorApprovedAccidental,
    int EvidenceReuseMatches,
    [property: JsonIgnore] bool ReturnedAndReplaced = false)
{
    public static ClaimHistoryCounts None { get; } = new(0, 0, 0);
}
