using System.Text.Json;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Catalog;
using Warranty.Domain.Claims;

namespace Warranty.AI.Harness.Tools.Implementations;

/// <summary>Names of the tools in the catalog (contracts/agents-and-tools.md).</summary>
public static class ToolNames
{
    public const string CustomerLookup = "customer_lookup";

    public const string ProductLookup = "product_lookup";

    public const string WarrantyLookup = "warranty_lookup";

    public const string InvoiceValidation = "invoice_validation";

    public const string ClaimHistoryLookup = "claim_history_lookup";

    public const string SearchPolicyKnowledge = "search_policy_knowledge";

    public const string SearchGlobalKnowledge = "search_global_knowledge";
}

/// <summary>Helpers shared by the tool implementations.</summary>
internal static class ToolSupport
{
    /// <summary>The input schema of a tool that takes no arguments: the run's claim is implied.</summary>
    public const string NoArgumentsSchema = """{"type":"object","additionalProperties":false,"properties":{}}""";

    public static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static HashSet<string> Callers(params string[] callers) => new(callers, StringComparer.Ordinal);

    /// <summary>
    /// The run's claim. Repositories are tenant-scoped, so a claim of another tenant is not found either;
    /// a missing claim is a harness fault and fails the call.
    /// </summary>
    public static async Task<Claim> RequireClaimAsync(IClaimRepository claims, Guid claimId, CancellationToken ct)
        => await claims.GetAsync(claimId, ct)
            ?? throw new InvalidOperationException($"Claim {claimId} of the run was not found in the current tenant.");

    /// <summary>
    /// The claim's catalog product, only when the claimed serial is registered to it (FR-003, data-model.md
    /// <c>catalog.product_serials</c>): a known model with an unknown serial is not in the catalog either. The
    /// catalog is tenant-scoped, so another tenant's products and serials are never consulted. Null means the
    /// claim is evaluated against the tenant's catalog-independent policy documents only.
    /// </summary>
    public static async Task<Product?> CatalogProductAsync(ICatalogRepository catalog, Claim claim, CancellationToken ct)
    {
        if (claim.ProductId is not { } productId)
        {
            return null;
        }

        var serial = await catalog.FindSerialAsync(claim.SerialNumber, ct);
        return serial is not null && serial.ProductId == productId ? await catalog.GetProductAsync(productId, ct) : null;
    }
}
