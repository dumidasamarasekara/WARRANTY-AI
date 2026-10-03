using Warranty.Application.Abstractions.Integrations;
using Warranty.Application.Abstractions.Persistence;

namespace Warranty.Integrations.Simulated;

/// <summary>
/// Simulated ERP serial registry over the tenant's catalog (<c>catalog.products</c>,
/// <c>catalog.product_serials</c>). A serial counts as registered only when it belongs to the
/// claimed model; a serial of another model is reported as not registered.
/// </summary>
internal sealed class SimulatedErpSerialRegistry(ICatalogRepository catalog) : IErpSerialRegistry
{
    public async Task<SerialLookup> LookupAsync(string modelCode, string serialNumber, CancellationToken ct)
    {
        var product = await catalog.FindProductByModelAsync(modelCode, ct);
        if (product is null)
        {
            return new SerialLookup(ModelFound: false, SerialRegistered: false, null, null, null);
        }

        var serial = await catalog.FindSerialAsync(serialNumber, ct);
        return new SerialLookup(
            ModelFound: true,
            SerialRegistered: serial is not null && serial.ProductId == product.Id,
            product.Id,
            product.Name,
            product.Category);
    }
}
