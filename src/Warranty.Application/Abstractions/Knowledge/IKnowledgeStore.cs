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
}

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
