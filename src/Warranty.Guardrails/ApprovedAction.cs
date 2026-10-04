using Warranty.Domain.Claims;

namespace Warranty.Guardrails;

/// <summary>The consequential action a guardrail evaluation allows (contracts/agents-and-tools.md).</summary>
public enum ActionKind
{
    FinalizeApproved,
    FinalizeRejected,
    RequestInformation,
    EscalateToReview,
}

/// <summary>
/// The only input the <c>ActionExecutor</c> accepts. It can be created only inside
/// <c>Warranty.Guardrails</c> (internal constructor, sealed), so no AI output can issue one.
/// </summary>
public sealed class ApprovedAction
{
    internal ApprovedAction(ActionKind kind, Guid tenantId, Guid claimId, Guid runId, IEnumerable<RequestedItem> requestedItems)
    {
        Kind = kind;
        TenantId = tenantId;
        ClaimId = claimId;
        RunId = runId;
        RequestedItems = requestedItems.ToArray();
    }

    public ActionKind Kind { get; }

    public Guid TenantId { get; }

    public Guid ClaimId { get; }

    public Guid RunId { get; }

    /// <summary>Items to request from the submitter; empty unless <see cref="Kind"/> is <see cref="ActionKind.RequestInformation"/>.</summary>
    public IReadOnlyList<RequestedItem> RequestedItems { get; }
}
