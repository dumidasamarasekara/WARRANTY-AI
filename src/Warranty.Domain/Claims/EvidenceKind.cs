namespace Warranty.Domain.Claims;

public enum EvidenceKind
{
    Invoice,
    Photo,
    Other,

    /// <summary>Proof of a protection plan (extended warranty); sanitized like an invoice (002 research R9).</summary>
    ProtectionPlanDocument,
}
