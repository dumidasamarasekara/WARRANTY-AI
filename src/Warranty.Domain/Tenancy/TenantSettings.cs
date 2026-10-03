namespace Warranty.Domain.Tenancy;

/// <summary>Per-tenant adjudication settings (FR-001, data-model.md tenancy.tenant_settings).</summary>
public sealed class TenantSettings
{
    private TenantSettings()
    {
        Currency = string.Empty;
        AlwaysReviewCategories = [];
    }

    public Guid TenantId { get; private set; }

    /// <summary>ISO 4217 currency of every claim value of the tenant.</summary>
    public string Currency { get; private set; }

    /// <summary>Claim values strictly above this limit go to human review.</summary>
    public decimal AutoApprovalLimit { get; private set; }

    /// <summary>Confidence strictly below this minimum goes to human review (0–100).</summary>
    public int MinConfidence { get; private set; }

    public bool AutoApproveEnabled { get; private set; }

    public bool AutoRejectEnabled { get; private set; }

    /// <summary>Product categories that always require a human decision.</summary>
    public IReadOnlyList<string> AlwaysReviewCategories { get; private set; }

    /// <summary>Risk score at or above which risk is High; any signal already makes it Medium (research R23).</summary>
    public int RiskHighThreshold { get; private set; }

    /// <summary>Optimistic concurrency token.</summary>
    public int Version { get; private set; }

    public static TenantSettings Create(
        Guid tenantId,
        string currency,
        decimal autoApprovalLimit,
        int minConfidence,
        bool autoApproveEnabled = true,
        bool autoRejectEnabled = true,
        IEnumerable<string>? alwaysReviewCategories = null,
        int riskHighThreshold = 60)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        }

        if (currency is null || currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException($"Currency '{currency}' must be a 3-letter upper-case ISO 4217 code.", nameof(currency));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(autoApprovalLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(minConfidence);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minConfidence, 100);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(riskHighThreshold);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(riskHighThreshold, 100);

        return new TenantSettings
        {
            TenantId = tenantId,
            Currency = currency,
            AutoApprovalLimit = decimal.Round(autoApprovalLimit, 2),
            MinConfidence = minConfidence,
            AutoApproveEnabled = autoApproveEnabled,
            AutoRejectEnabled = autoRejectEnabled,
            AlwaysReviewCategories = (alwaysReviewCategories ?? [])
                .Select(c => c.Trim().ToLowerInvariant())
                .Where(c => c.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            RiskHighThreshold = riskHighThreshold,
            Version = 1,
        };
    }

    /// <summary>True when the claim value must go to a reviewer; a value equal to the limit is within it.</summary>
    public bool IsAboveAutoApprovalLimit(decimal claimValue) => claimValue > AutoApprovalLimit;

    public bool IsBelowMinConfidence(int confidence) => confidence < MinConfidence;

    public bool AlwaysRequiresReview(string? productCategory)
        => productCategory is not null
            && AlwaysReviewCategories.Contains(productCategory.Trim().ToLowerInvariant(), StringComparer.Ordinal);
}
