using System.Text.Json;
using System.Text.Json.Serialization;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Domain.Common;
using Warranty.Domain.Integration;
using Warranty.Domain.Policies;
using Warranty.Knowledge.Ingestion;

namespace Warranty.MigrationService.Seeding;

public sealed record TenantSeed(Guid Id, string Slug, string DisplayName, string Channel, TenantSettingsSeed Settings)
{
    public string KnowledgeNamespace => $"tenant-{Slug}";
}

public sealed record TenantSettingsSeed(
    string Currency,
    decimal AutoApprovalLimit,
    int MinConfidence,
    bool AutoApproveEnabled,
    bool AutoRejectEnabled,
    IReadOnlyList<string> AlwaysReviewCategories,
    int RiskHighThreshold);

public sealed record ProductSeed(Guid Id, string ModelCode, string Name, string Category, decimal ClaimValue);

public sealed record SerialSeed(string SerialNumber, string ModelCode, DateOnly? ManufacturedOn, string? ReservedFor);

public sealed record CustomerSeed(
    Guid Id, string FullName, string Email, string? Phone, string? AddressLine, string? City, string? PostalCode, string Country);

/// <summary>
/// One repair centre of <c>service-centers.json</c> (data-model.md <c>integration.service_centers</c>).
/// <see cref="Address"/> and <see cref="OpeningHours"/> are shown to claimants and required for an
/// active centre; <see cref="Contact"/> is staff-only. A lower <see cref="Priority"/> is suggested first.
/// </summary>
public sealed record ServiceCenterSeed(
    Guid Id,
    Region Region,
    string Name,
    IReadOnlyList<string> Capabilities,
    string? Address = null,
    string? OpeningHours = null,
    string? Contact = null,
    bool IsActive = true,
    int Priority = ServiceCenter.DefaultPriority);

/// <summary>
/// The validated <c>remedies</c> front-matter block of one seeded policy version; null
/// <see cref="RemedyRules"/> means the version gives no remedy (FR-012).
/// </summary>
public sealed record PolicyRemediesSeed(string BlobPath, string PolicyCode, int Version, RemedyRules? RemedyRules);

/// <summary>
/// A finalized claim seeded as history (quickstart S18). Every <c>*DaysAgo</c> value is a signed day
/// offset from the seeding day (negative = in the past), so the history keeps its distance to "today".
/// </summary>
public sealed record HistoricalClaimSeed(
    Guid Id,
    string Reference,
    string CustomerEmail,
    string ModelCode,
    string SerialNumber,
    Region Region,
    string PurchasePlace,
    decimal PurchasePrice,
    string ProblemDescription,
    int PurchasedDaysAgo,
    int SubmittedDaysAgo,
    int FinalizedDaysAgo,
    string Outcome,
    string Explanation)
{
    public DateOnly PurchaseDate(DateTimeOffset seedingTime) => DateOnly.FromDateTime(seedingTime.UtcDateTime).AddDays(PurchasedDaysAgo);

    public DateTimeOffset SubmittedAt(DateTimeOffset seedingTime) => seedingTime.AddDays(SubmittedDaysAgo);

    public DateTimeOffset FinalizedAt(DateTimeOffset seedingTime) => seedingTime.AddDays(FinalizedDaysAgo);
}

/// <summary>
/// One knowledge source file. <see cref="BlobPath"/> is its path in the <c>knowledge-sources</c>
/// container (<c>global/…</c> or <c>tenant-{slug}/…</c>) and the source path recorded at ingestion.
/// Line endings are normalized so the checksum does not depend on how the repository was checked out.
/// </summary>
public sealed record KnowledgeSeed(string BlobPath, string Content)
{
    public KnowledgeSourceDocument ToDocument() => new(BlobPath, Content);
}

public sealed record TenantSeedSet(
    TenantSeed Tenant,
    IReadOnlyList<ProductSeed> Products,
    IReadOnlyList<SerialSeed> Serials,
    IReadOnlyList<CustomerSeed> Customers,
    IReadOnlyList<ServiceCenterSeed> ServiceCenters,
    IReadOnlyList<HistoricalClaimSeed> HistoricalClaims,
    IReadOnlyList<KnowledgeSeed> Knowledge,
    IReadOnlyList<PolicyRemediesSeed> PolicyRemedies);

public sealed record SeedSet(IReadOnlyList<TenantSeedSet> Tenants, IReadOnlyList<KnowledgeSeed> GlobalKnowledge);

/// <summary>
/// Reads the synthetic seed folder (data-model.md §Seed data): <c>tenants/{slug}/tenant.json</c>,
/// <c>products.json</c>, <c>serials.json</c>, <c>customers.json</c>, <c>service-centers.json</c>,
/// optional <c>historical-claims.json</c> and <c>policies/*.md</c>, plus <c>global/*.md</c>. Every
/// file is checked for references the database would only reject later (unknown model codes,
/// customers or a folder name that differs from the slug).
/// </summary>
public static class SeedFiles
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static SeedSet Load(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(Path.Combine(root, "tenants")))
        {
            throw new DirectoryNotFoundException($"Seed folder '{root}' has no 'tenants' folder.");
        }

        var tenants = Directory.GetDirectories(Path.Combine(root, "tenants"))
            .Order(StringComparer.Ordinal)
            .Select(LoadTenant)
            .ToList();
        var global = Markdown(Path.Combine(root, "global"), "global");
        return new SeedSet(tenants, global);
    }

    private static TenantSeedSet LoadTenant(string folder)
    {
        var tenant = Read<TenantSeed>(folder, "tenant.json");
        if (!string.Equals(Path.GetFileName(folder), tenant.Slug, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Seed folder '{folder}' holds tenant '{tenant.Slug}'; the folder must be named after the slug.");
        }

        var products = Read<List<ProductSeed>>(folder, "products.json");
        var serials = Read<List<SerialSeed>>(folder, "serials.json");
        var customers = Read<List<CustomerSeed>>(folder, "customers.json");
        var centers = Read<List<ServiceCenterSeed>>(folder, "service-centers.json");
        var history = File.Exists(Path.Combine(folder, "historical-claims.json"))
            ? Read<HistoricalClaimsFile>(folder, "historical-claims.json").Claims
            : [];

        var models = products.Select(p => p.ModelCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var emails = customers.Select(c => c.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var serialNumbers = serials.Select(s => s.SerialNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var errors = serials.Where(s => !models.Contains(s.ModelCode))
            .Select(s => $"serial {s.SerialNumber} names unknown model {s.ModelCode}")
            .Concat(history.Where(h => !models.Contains(h.ModelCode)).Select(h => $"history claim {h.Reference} names unknown model {h.ModelCode}"))
            .Concat(history.Where(h => !serialNumbers.Contains(h.SerialNumber)).Select(h => $"history claim {h.Reference} names unknown serial {h.SerialNumber}"))
            .Concat(history.Where(h => !emails.Contains(h.CustomerEmail)).Select(h => $"history claim {h.Reference} names unknown customer {h.CustomerEmail}"))
            .Concat(history.Where(h => !(h.PurchasedDaysAgo <= h.SubmittedDaysAgo && h.SubmittedDaysAgo <= h.FinalizedDaysAgo && h.FinalizedDaysAgo <= 0))
                .Select(h => $"history claim {h.Reference} must be purchased, submitted and finalized in that order, in the past"))
            .Concat(centers.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => $"service center {g.Key} is listed more than once"))
            .Concat(centers.Where(c => c.Priority < 0).Select(c => $"service center {c.Name} has a negative priority"))
            .ToList();
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"Seed folder '{folder}': {string.Join("; ", errors)}.");
        }

        var knowledge = Markdown(Path.Combine(folder, "policies"), $"{tenant.KnowledgeNamespace}/policies");
        var remedies = knowledge.Select(k => Remedies(folder, k)).OfType<PolicyRemediesSeed>().ToList();
        return new TenantSeedSet(tenant, products, serials, customers, centers, history, knowledge, remedies);
    }

    /// <summary>
    /// Reads the <c>remedies</c> front-matter block of a warranty policy (research R1): first against the
    /// embedded <c>remedy-rules.schema.json</c>, then through the ingestion parser and
    /// <see cref="RemedyRules.Validate"/>, so an invalid block stops the seed before anything is written.
    /// Null for a source that is not a warranty policy.
    /// </summary>
    private static PolicyRemediesSeed? Remedies(string folder, KnowledgeSeed seed)
    {
        var problems = RemedyRulesSchema.Validate(seed.Content);
        KnowledgeSource? source = null;
        if (problems.Count == 0)
        {
            try
            {
                source = FrontMatterParser.Parse(seed.ToDocument());
                source.RemedyRules?.Validate();
            }
            catch (Exception ex) when (ex is InvalidKnowledgeSourceException or ArgumentException)
            {
                problems = [ex.Message];
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException(
                $"Seed folder '{folder}': policy {seed.BlobPath} has an invalid remedies block: {string.Join("; ", problems)}.");
        }

        return source is { DocumentType: DocumentType.WarrantyPolicy, PolicyCode: { } code }
            ? new PolicyRemediesSeed(seed.BlobPath, code, source.Version, source.RemedyRules)
            : null;
    }

    private static List<KnowledgeSeed> Markdown(string folder, string blobPrefix)
        => Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.md")
                .Order(StringComparer.Ordinal)
                .Select(path => new KnowledgeSeed($"{blobPrefix}/{Path.GetFileName(path)}", File.ReadAllText(path).ReplaceLineEndings("\n")))
                .ToList()
            : [];

    private static T Read<T>(string folder, string file)
    {
        var path = Path.Combine(folder, file);
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new InvalidDataException($"Seed file '{path}' is empty.");
    }

    private sealed record HistoricalClaimsFile(IReadOnlyList<HistoricalClaimSeed> Claims);
}
