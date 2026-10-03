using Microsoft.Extensions.DependencyInjection;

namespace Warranty.Knowledge;

public static class DependencyInjection
{
    /// <summary>Registers knowledge ingestion and tenant-scoped retrieval (contracts/rag.md).</summary>
    public static IServiceCollection AddWarrantyKnowledge(this IServiceCollection services)
    {
        // Ingestion (T032) and retrieval (T033) register here.
        return services;
    }
}
