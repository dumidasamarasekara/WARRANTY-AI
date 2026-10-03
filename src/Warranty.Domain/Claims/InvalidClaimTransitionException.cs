namespace Warranty.Domain.Claims;

/// <summary>Raised when a claim state change is not allowed by the claim state machine.</summary>
public sealed class InvalidClaimTransitionException : InvalidOperationException
{
    public InvalidClaimTransitionException()
    {
    }

    public InvalidClaimTransitionException(string message)
        : base(message)
    {
    }

    public InvalidClaimTransitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public InvalidClaimTransitionException(ClaimStatus from, string action)
        : base($"Cannot {action} a claim in status {from}.")
    {
        From = from;
        Action = action;
    }

    public ClaimStatus? From { get; }

    public string? Action { get; }
}
