using System.Text.RegularExpressions;

namespace Warranty.Domain.Tenancy;

public enum TenantStatus
{
    Active,
    Suspended,
}

/// <summary>An independent manufacturer whose warranty program runs on the platform (platform-owned row).</summary>
public sealed partial class Tenant
{
    private Tenant()
    {
        Slug = DisplayName = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>Lower-case slug; names the knowledge namespace and the blob container.</summary>
    public string Slug { get; private set; }

    public string DisplayName { get; private set; }

    public TenantStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Knowledge namespace partition owned by this tenant (research R6).</summary>
    public string KnowledgeNamespace => $"tenant-{Slug}";

    /// <summary>Blob container holding this tenant's evidence files (research R11).</summary>
    public string BlobContainer => $"tenant-{Slug}";

    public static Tenant Create(Guid id, string slug, string displayName, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Tenant ID is required.", nameof(id));
        }

        if (slug is null || !SlugPattern().IsMatch(slug))
        {
            throw new ArgumentException($"Tenant slug '{slug}' must match ^[a-z][a-z0-9-]{{2,30}}$.", nameof(slug));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        return new Tenant
        {
            Id = id,
            Slug = slug,
            DisplayName = displayName.Trim(),
            Status = TenantStatus.Active,
            CreatedAt = createdAt,
        };
    }

    public void Suspend() => Status = TenantStatus.Suspended;

    [GeneratedRegex("^[a-z][a-z0-9-]{2,30}$")]
    private static partial Regex SlugPattern();
}
