using System.Globalization;
using System.Text.Json;
using Warranty.AI.Harness.Agents;
using Warranty.Application.Abstractions.Persistence;

namespace Warranty.AI.Harness.Tools.Implementations;

/// <summary>
/// <c>product_lookup</c> (Intake, Evidence): whether a model code is in the tenant's catalog and whether
/// a serial number is registered to that model (simulated ERP). The catalog is tenant-scoped, so another
/// tenant's products and serials are never found. A serial registered to a different model counts as not
/// registered.
/// </summary>
public sealed class ProductLookupTool(ICatalogRepository catalog) : ITool
{
    public const int MaxIdentifierLength = 64;

    // Only keywords the providers' strict tool mode accepts; lengths are checked in code, as in the contract schemas.
    private const string Schema = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "modelCode": { "type": "string", "description": "Product model code, e.g. AUR-TAB10." },
            "serialNumber": { "type": "string", "description": "Serial number of the unit." }
          },
          "required": ["modelCode", "serialNumber"]
        }
        """;

    public ToolDescriptor Descriptor { get; } = new(
        ToolNames.ProductLookup,
        "Looks up a product model and serial number in the manufacturer's catalog. Returns whether the model was found, "
        + "whether the serial number is registered to that model, and the product name and category.",
        ToolSupport.Schema(Schema),
        ToolSideEffect.ReadOnly,
        ToolSupport.Callers(AgentNames.Intake, AgentNames.Evidence));

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolInvocationContext ctx, CancellationToken ct)
    {
        var modelCode = arguments.GetProperty("modelCode").GetString()!;
        var serialNumber = arguments.GetProperty("serialNumber").GetString()!;
        if (!IsIdentifier(modelCode) || !IsIdentifier(serialNumber))
        {
            return ToolResult.Error($"modelCode and serialNumber must be non-blank and at most {MaxIdentifierLength} characters.");
        }

        var result = await LookupAsync(modelCode, serialNumber, ct);
        return ToolResult.Ok(
            result,
            string.Create(CultureInfo.InvariantCulture, $"found={result.Found}, serialRegistered={result.SerialRegistered}, category={result.Category ?? "-"}"));
    }

    public async Task<ProductLookupResult> LookupAsync(string modelCode, string serialNumber, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        var product = await catalog.FindProductByModelAsync(modelCode, ct);
        if (product is null)
        {
            return new ProductLookupResult(false, false, null, null);
        }

        var serial = await catalog.FindSerialAsync(serialNumber, ct);
        return new ProductLookupResult(true, serial is not null && serial.ProductId == product.Id, product.Name, product.Category);
    }

    private static bool IsIdentifier(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxIdentifierLength;
}

/// <summary>Result of <c>product_lookup</c>.</summary>
/// <param name="Found">The model code is in the tenant's catalog.</param>
/// <param name="SerialRegistered">The serial number is registered to that model.</param>
/// <param name="ProductName">Catalog name; null when not found.</param>
/// <param name="Category">Lower-case catalog category; null when not found.</param>
public sealed record ProductLookupResult(bool Found, bool SerialRegistered, string? ProductName, string? Category);
