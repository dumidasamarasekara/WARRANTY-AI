using Microsoft.Extensions.DependencyInjection;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Knowledge.Ingestion;

namespace Warranty.Knowledge;

public static class DependencyInjection
{
    /// <summary>Registers knowledge ingestion and tenant-scoped retrieval (contracts/rag.md).</summary>
    public static IServiceCollection AddWarrantyKnowledge(this IServiceCollection services)
    {
        services.AddScoped<KnowledgeSourceValidator>();
        services.AddScoped<IKnowledgeIngestor, KnowledgeIngestor>();

        // Retrieval (T033) registers here.
        return services;
    }
}
