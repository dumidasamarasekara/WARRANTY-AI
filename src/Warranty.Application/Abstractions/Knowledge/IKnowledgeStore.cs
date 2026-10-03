using Warranty.Domain.Common;
using Warranty.Domain.Policies;

namespace Warranty.Application.Abstractions.Knowledge;

/// <summary>
/// Storage of indexed knowledge (research R6): the pgvector knowledge database today, replaceable by
/// a dedicated vector database. Like every tenant-scoped port it takes no tenant ID — a document in a
/// tenant namespace is written for, and only for, the tenant in <see cref="ITenantContext"/>, and
/// <c>global</c> documents only without a tenant context (platform ingestion).
/// </summary>
public interface IKnowledgeStore
{
    /// <summary>The indexed document with this source in the namespace, if any.</summary>
    Task<StoredKnowledgeDocument?> FindDocumentAsync(string knowledgeNamespace, string sourceRef, CancellationToken ct);

    /// <summary>
    /// Creates the namespace partition when needed and writes the document with exactly these chunks,
    /// replacing the chunks of an earlier index of the same source. Returns the document ID.
    /// </summary>
    Task<Guid> SaveDocumentAsync(KnowledgeDocumentDraft document, IReadOnlyList<KnowledgeChunkDraft> chunks, CancellationToken ct);

    /// <summary>The distinct documents with at least one chunk that passes the hard filters.</summary>
    Task<IReadOnlyList<KnowledgeDocumentMatch>> FindDocumentsAsync(KnowledgeFilter filter, CancellationToken ct);

    /// <summary>
    /// The <paramref name="topK"/> chunks that pass the hard filters, ranked by cosine similarity to
    /// <paramref name="embedding"/> (filter first, rank second — research R7). Score = 1 − cosine distance.
    /// </summary>
    Task<IReadOnlyList<RetrievedChunk>> SearchChunksAsync(KnowledgeFilter filter, ReadOnlyMemory<float> embedding, int topK, CancellationToken ct);
}

/// <summary>
/// Hard metadata filters, applied in the store's query before any similarity ranking
/// (contracts/rag.md rule 2). Built by the retriever from the tenant context, never from model output.
/// </summary>
/// <param name="Namespaces">Namespaces to read; the store's own namespace isolation still applies.</param>
/// <param name="DocumentTypes">Allowed document types; null for any.</param>
/// <param name="Applicability">Product, region and purchase-date filters; null for none (see <see cref="PolicyApplicability"/>).</param>
/// <param name="Classifications">Classifications the principal may read.</param>
/// <param name="Roles">Principal roles; a chunk matches when its <c>allowed_roles</c> overlap them.</param>
/// <param name="DocumentIds">Restricts to these documents (e.g. the selected policy version); null for any.</param>
/// <param name="ClauseTypes">Restricts to these clause types; null for any.</param>
public sealed record KnowledgeFilter(
    IReadOnlyList<string> Namespaces,
    IReadOnlyList<DocumentType>? DocumentTypes,
    PolicyApplicability? Applicability,
    IReadOnlyList<DocumentClassification> Classifications,
    IReadOnlyList<string> Roles,
    IReadOnlyList<Guid>? DocumentIds = null,
    IReadOnlyList<ClauseType>? ClauseTypes = null);

/// <summary>A document that passed the hard filters; policy documents carry their policy version.</summary>
public sealed record KnowledgeDocumentMatch(Guid DocumentId, string Namespace, string Title, int Version, Guid? PolicyVersionId);

/// <summary>What re-ingestion needs to know about an indexed document.</summary>
public sealed record StoredKnowledgeDocument(
    Guid Id, string Namespace, string SourceRef, string Checksum, string EmbeddingModel, int EmbeddingDim, int ChunkCount);

/// <summary>A document to index; its filter fields are copied onto every chunk.</summary>
public sealed record KnowledgeDocumentDraft(
    string Namespace,
    DocumentType DocumentType,
    string SourceRef,
    string Title,
    int Version,
    string? ProductCategory,
    string? ProductModel,
    IReadOnlyList<Region> Regions,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    DocumentClassification Classification,
    IReadOnlyList<string> AllowedRoles,
    string EmbeddingModel,
    int EmbeddingDim,
    string Checksum);

/// <summary>One embedded chunk; policy chunks carry their version, clause type and exclusion code (research R26).</summary>
public sealed record KnowledgeChunkDraft(
    int ChunkIndex,
    string? ClauseKey,
    string? SectionTitle,
    string Text,
    ReadOnlyMemory<float> Embedding,
    Guid? PolicyVersionId = null,
    ClauseType? ClauseType = null,
    ExclusionCode? ExclusionCode = null);
