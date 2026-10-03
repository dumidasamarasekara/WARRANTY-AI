namespace Warranty.Application.Abstractions.Knowledge;

/// <summary>
/// Tenant-aware retrieval. No method takes a tenant ID: the namespace and roles come from
/// <see cref="ITenantContext"/>, and calling without a resolved tenant throws (contracts/rag.md).
/// </summary>
public interface IKnowledgeRetriever
{
    Task<RetrievalResult> RetrievePolicyClausesAsync(PolicyRetrievalQuery query, CancellationToken ct);

    Task<RetrievalResult> SearchAsync(KnowledgeSearchQuery query, CancellationToken ct);
}

/// <summary>Ingests global and tenant knowledge; used by the migration service only.</summary>
public interface IKnowledgeIngestor
{
    Task<IngestionResult> IngestAsync(KnowledgeSourceDocument document, CancellationToken ct);
}

/// <summary>Loads the case knowledge (claim facts and evidence) for one run of the current tenant.</summary>
public interface ICaseKnowledgeProvider
{
    Task<CaseContext> GetCaseContextAsync(Guid claimId, int round, CancellationToken ct);
}
