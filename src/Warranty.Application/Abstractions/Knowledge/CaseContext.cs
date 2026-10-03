using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.Application.Abstractions.Knowledge;

/// <summary>
/// Everything the harness knows about one claim round. The customer appears only as a redacted view:
/// identifiers are placeholders and never reach a prompt (FR-006a, research R28).
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
    int AutoInfoRequestCount);

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
/// </summary>
public sealed record ClaimHistoryCounts(
    int DuplicateClaimsForSerial,
    int PriorApprovedAccidental,
    int EvidenceReuseMatches)
{
    public static ClaimHistoryCounts None { get; } = new(0, 0, 0);
}
