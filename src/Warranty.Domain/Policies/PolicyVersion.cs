using Warranty.Domain.Common;

namespace Warranty.Domain.Policies;

/// <summary>
/// One effective-dated version of a warranty policy. The version in effect on the purchase date
/// applies (clarification Q1); applicability is decided only from these structured fields.
/// </summary>
public sealed class PolicyVersion
{
    private PolicyVersion()
    {
        Regions = [];
        ProductCategories = [];
        Terms = null!;
        SourceBlobPath = Checksum = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid PolicyId { get; private set; }

    public int Version { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    /// <summary>Null means open-ended.</summary>
    public DateOnly? EffectiveTo { get; private set; }

    public IReadOnlyList<Region> Regions { get; private set; }

    /// <summary>Empty means the version applies to every product category.</summary>
    public IReadOnlyList<string> ProductCategories { get; private set; }

    public CoverageTerms Terms { get; private set; }

    /// <summary>Original Markdown in the <c>knowledge-sources</c> container.</summary>
    public string SourceBlobPath { get; private set; }

    /// <summary>SHA-256 of the source; unchanged sources are not re-ingested.</summary>
    public string Checksum { get; private set; }

    public static PolicyVersion Create(
        Guid id,
        Guid tenantId,
        Guid policyId,
        int version,
        DateOnly effectiveFrom,
        DateOnly? effectiveTo,
        IEnumerable<Region> regions,
        IEnumerable<string> productCategories,
        CoverageTerms terms,
        string sourceBlobPath,
        string checksum)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || policyId == Guid.Empty)
        {
            throw new ArgumentException("Version, tenant and policy IDs are required.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        if (effectiveTo is { } end && end < effectiveFrom)
        {
            throw new ArgumentException("A version cannot end before it starts.", nameof(effectiveTo));
        }

        var regionList = regions.Distinct().ToArray();
        if (regionList.Length == 0)
        {
            throw new ArgumentException("A policy version must apply to at least one region.", nameof(regions));
        }

        ArgumentNullException.ThrowIfNull(terms);
        terms.Validate(regionList);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBlobPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(checksum);

        return new PolicyVersion
        {
            Id = id,
            TenantId = tenantId,
            PolicyId = policyId,
            Version = version,
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo,
            Regions = regionList,
            ProductCategories = productCategories
                .Select(c => c.Trim().ToLowerInvariant())
                .Where(c => c.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Terms = terms,
            SourceBlobPath = sourceBlobPath,
            Checksum = checksum,
        };
    }

    /// <summary>True when the version was in effect on the purchase date (inclusive bounds).</summary>
    public bool IsInEffectOn(DateOnly purchaseDate)
        => EffectiveFrom <= purchaseDate && (EffectiveTo is null || purchaseDate <= EffectiveTo);

    /// <summary>Version selection rule: in effect on the purchase date, for the claim's region and category.</summary>
    public bool AppliesTo(DateOnly purchaseDate, Region region, string? productCategory)
        => IsInEffectOn(purchaseDate)
            && Regions.Contains(region)
            && (ProductCategories.Count == 0
                || (productCategory is not null
                    && ProductCategories.Contains(productCategory.Trim().ToLowerInvariant(), StringComparer.Ordinal)));

    /// <summary>Versions of one policy must not overlap in time (validated on seed).</summary>
    public bool OverlapsInTime(PolicyVersion other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var thisEnd = EffectiveTo ?? DateOnly.MaxValue;
        var otherEnd = other.EffectiveTo ?? DateOnly.MaxValue;
        return EffectiveFrom <= otherEnd && other.EffectiveFrom <= thisEnd;
    }
}
