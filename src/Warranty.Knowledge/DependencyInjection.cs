using Microsoft.Extensions.DependencyInjection;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Knowledge.Ingestion;
using Warranty.Knowledge.Retrieval;

namespace Warranty.Knowledge;

public static class DependencyInjection
{
    /// <summary>
    /// Registers knowledge ingestion and tenant-scoped retrieval (contracts/rag.md). Requires the
    /// knowledge store, repositories, security event writer and a <see cref="TimeProvider"/> from
    /// infrastructure, and the gateway with its PII redactor.
    /// </summary>
    public static IServiceCollection AddWarrantyKnowledge(this IServiceCollection services)
    {
        services.AddScoped<KnowledgeSourceValidator>();
        services.AddScoped<IKnowledgeIngestor, KnowledgeIngestor>();
        services.AddScoped<IKnowledgeRetriever, KnowledgeRetriever>();
        return services;
    }
}
