namespace Warranty.Domain.Claims;

/// <summary>How a claim was submitted: a tenant's claimant channel or a claims agent (FR-002).</summary>
public enum ClaimChannel
{
    ClaimantPortal,
    AgentPortal,
}
