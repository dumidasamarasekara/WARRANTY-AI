namespace Warranty.Domain.Claims;

/// <summary>Who produced a claim's final outcome: the guardrail-approved automation or a reviewer.</summary>
public enum DecidedBy
{
    System,
    Reviewer,
}
