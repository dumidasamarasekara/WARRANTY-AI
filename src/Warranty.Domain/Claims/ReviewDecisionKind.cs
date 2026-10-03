namespace Warranty.Domain.Claims;

/// <summary>A reviewer's action on an escalated claim (FR-034).</summary>
public enum ReviewDecisionKind
{
    Approve,
    Reject,
    RequestInformation,
}
