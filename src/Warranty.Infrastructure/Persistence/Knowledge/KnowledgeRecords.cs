using Pgvector;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Domain.Common;
using Warranty.Domain.Policies;

namespace Warranty.Infrastructure.Persistence.Knowledge;

/// <summary>One ingested source document (<c>knowledge.knowledge_documents</c>, data-model.md).</summary>
public sealed class KnowledgeDocument
{
    public Guid Id { get; set; }

    /// <summary><c>global</c> or <c>tenant-{slug}</c>.</summary>
    public required string Namespace { get; set; }

    /// <summary>Null only for the <c>global</c> namespace.</summary>
    public Guid? TenantId { get; set; }

    public DocumentType DocumentType { get; set; }

    /// <summary>Origin of the document, e.g. <c>policy_version:{id}</c>; unique per namespace.</summary>
    public required string SourceRef { get; set; }

    public required string Title { get; set; }

    public int Version { get; set; }

    /// <summary>Null means all categories.</summary>
    public string? ProductCategory { get; set; }

    /// <summary>Null means all models.</summary>
    public string? ProductModel { get; set; }

    /// <summary>Empty means all regions.</summary>
    public Region[] Regions { get; set; } = [];

    public DateOnly? EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public DocumentClassification Classification { get; set; }

    public string[] AllowedRoles { get; set; } = [];

    public required string EmbeddingModel { get; set; }

    public int EmbeddingDim { get; set; }

    /// <summary>SHA-256 of the source; an unchanged checksum skips re-indexing.</summary>
    public required string Checksum { get; set; }
}

/// <summary>
/// One embedded chunk (<c>knowledge.knowledge_chunks</c>, partitioned by namespace). The document's
/// filter columns are copied onto every chunk so hard filters run before similarity ranking.
/// </summary>
public sealed class KnowledgeChunk
{
    public Guid Id { get; set; }

    public required string Namespace { get; set; }

    public Guid? TenantId { get; set; }

    public Guid DocumentId { get; set; }

    public int ChunkIndex { get; set; }

    /// <summary>Policy clause key such as <c>AUR-WP-3.2</c>; repeated on every chunk of a split clause.</summary>
    public string? ClauseKey { get; set; }

    public string? SectionTitle { get; set; }

    public required string Text { get; set; }

    public required Vector Embedding { get; set; }

    public DocumentType DocumentType { get; set; }

    public string? ProductCategory { get; set; }

    public string? ProductModel { get; set; }

    public Region[] Regions { get; set; } = [];

    public DateOnly? EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public DocumentClassification Classification { get; set; }

    public string[] AllowedRoles { get; set; } = [];

    public Guid? PolicyVersionId { get; set; }

    public ClauseType? ClauseType { get; set; }

    public ExclusionCode? ExclusionCode { get; set; }
}
