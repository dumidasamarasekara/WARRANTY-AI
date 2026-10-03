namespace Warranty.Domain.Claims;

/// <summary>
/// Claim risk level (FR-017). <see cref="Low"/> means no risk signal of any kind; any signal makes
/// risk at least <see cref="Medium"/> (research R23).
/// </summary>
public enum RiskLevel
{
    Low,
    Medium,
    High,
}

/// <summary>Severity of one risk signal; weights 10 / 25 / 40 feed the risk score (research R23).</summary>
public enum RiskSeverity
{
    Low,
    Medium,
    High,
}
