using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Domain.Policies;

namespace Warranty.Infrastructure.Persistence.Knowledge;

/// <summary>
/// The pgvector <see cref="IKnowledgeStore"/> (research R6). A tenant-namespace document is written
/// only for the tenant in context, and a <c>global</c> one only without a tenant context — then the
/// connection runs in a <see cref="NoTenantScope"/>, so no tenant namespace is visible to it. Writes
/// need the knowledge database owner (<c>warranty_app</c> may only read), which only the migration
/// service connects as (research R8). Reads apply the retriever's hard filters in SQL before ranking by
/// cosine distance, on top of the context's namespace query filter and row-level security.
/// </summary>
internal sealed class KnowledgeStore(KnowledgeDbContext db, ITenantContext tenantContext) : IKnowledgeStore
{
    public async Task<StoredKnowledgeDocument?> FindDocumentAsync(string knowledgeNamespace, string sourceRef, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);
        using var platform = PlatformScopeFor(knowledgeNamespace);

        var document = await db.Documents.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Namespace == knowledgeNamespace && d.SourceRef == sourceRef, ct);
        if (document is null)
        {
            return null;
        }

        var chunkCount = await db.Chunks.CountAsync(c => c.Namespace == knowledgeNamespace && c.DocumentId == document.Id, ct);
        return new StoredKnowledgeDocument(
            document.Id, document.Namespace, document.SourceRef, document.Checksum, document.EmbeddingModel, document.EmbeddingDim, chunkCount);
    }

    public async Task<Guid> SaveDocumentAsync(KnowledgeDocumentDraft document, IReadOnlyList<KnowledgeChunkDraft> chunks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chunks);
        var tenantId = TenantFor(document.Namespace);
        if (document.EmbeddingDim != KnowledgeDbContext.EmbeddingDimensions
            || chunks.Any(c => c.Embedding.Length != KnowledgeDbContext.EmbeddingDimensions))
        {
            throw new InvalidOperationException(
                $"Knowledge chunks need {KnowledgeDbContext.EmbeddingDimensions}-dimension embeddings; '{document.SourceRef}' has {document.EmbeddingDim}.");
        }

        using var platform = PlatformScopeFor(document.Namespace);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async token =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                await db.Database.ExecuteSqlAsync($"SELECT knowledge.ensure_namespace({document.Namespace})", token);

                var stored = await db.Documents
                    .SingleOrDefaultAsync(d => d.Namespace == document.Namespace && d.SourceRef == document.SourceRef, token);
                if (stored is null)
                {
                    stored = new KnowledgeDocument
                    {
                        Id = Guid.CreateVersion7(),
                        Namespace = document.Namespace,
                        SourceRef = document.SourceRef,
                        Title = document.Title,
                        EmbeddingModel = document.EmbeddingModel,
                        Checksum = document.Checksum,
                    };
                    db.Documents.Add(stored);
                }
                else
                {
                    await db.Chunks.Where(c => c.Namespace == document.Namespace && c.DocumentId == stored.Id).ExecuteDeleteAsync(token);
                }

                Apply(stored, document, tenantId);
                db.Chunks.AddRange(chunks.Select(chunk => ToChunk(chunk, stored)));
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return stored.Id;
            },
            ct);
    }

    public async Task<IReadOnlyList<KnowledgeDocumentMatch>> FindDocumentsAsync(KnowledgeFilter filter, CancellationToken ct)
    {
        var rows = await DocumentsQuery(filter).ToListAsync(ct);
        return rows.Select(d => new KnowledgeDocumentMatch(d.Id, d.Namespace, d.Title, d.Version, d.PolicyVersionId)).ToList();
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchChunksAsync(
        KnowledgeFilter filter, ReadOnlyMemory<float> embedding, int topK, CancellationToken ct)
    {
        var rows = await ChunksQuery(filter, new Vector(embedding), topK).ToListAsync(ct);
        return rows.Select(r => new RetrievedChunk(
            r.Id, r.Namespace, r.DocumentId, r.Title, r.Version, r.ClauseKey, r.SectionTitle, r.Text, r.EffectiveFrom, r.EffectiveTo,
            1d - r.Distance, r.TenantId, r.PolicyVersionId, r.ClauseType, r.ExclusionCode)).ToList();
    }

    /// <summary>Distinct documents of the filtered chunks (exposed for SQL translation tests).</summary>
    internal IQueryable<DocumentRow> DocumentsQuery(KnowledgeFilter filter)
        => Filtered(filter)
            .Join(db.Documents, c => c.DocumentId, d => d.Id, (c, d) => new { d.Id, d.Namespace, d.Title, d.Version, c.PolicyVersionId })
            .Distinct()
            .Select(d => new DocumentRow(d.Id, d.Namespace, d.Title, d.Version, d.PolicyVersionId));

    /// <summary>Filtered chunks ranked by cosine distance (exposed for SQL translation tests).</summary>
    internal IQueryable<ChunkRow> ChunksQuery(KnowledgeFilter filter, Vector embedding, int topK)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        return Filtered(filter)
            .Join(db.Documents, c => c.DocumentId, d => d.Id, (c, d) => new { Chunk = c, d.Title, d.Version, Distance = c.Embedding.CosineDistance(embedding) })
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Chunk.Id)
            .Take(topK)
            .Select(x => new ChunkRow(
                x.Chunk.Id, x.Chunk.Namespace, x.Chunk.TenantId, x.Chunk.DocumentId, x.Title, x.Version, x.Chunk.ClauseKey,
                x.Chunk.SectionTitle, x.Chunk.Text, x.Chunk.EffectiveFrom, x.Chunk.EffectiveTo, x.Distance,
                x.Chunk.PolicyVersionId, x.Chunk.ClauseType, x.Chunk.ExclusionCode));
    }

    /// <summary>
    /// The hard filters of contracts/rag.md rule 2, in SQL: namespace, document type, category/model
    /// (or all), region (or all), effective dates around the purchase date, classification and an
    /// overlap of <c>allowed_roles</c> with the principal's roles (none → nothing matches).
    /// </summary>
    private IQueryable<KnowledgeChunk> Filtered(KnowledgeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var namespaces = filter.Namespaces.ToArray();
        var classifications = filter.Classifications.ToArray();
        var roles = filter.Roles.ToArray();

        var query = db.Chunks.AsNoTracking()
            .Where(c => namespaces.Contains(c.Namespace))
            .Where(c => classifications.Contains(c.Classification))
            .Where(c => c.AllowedRoles.Any(r => roles.Contains(r)));

        if (filter.DocumentTypes is { } documentTypes)
        {
            var types = documentTypes.ToArray();
            query = query.Where(c => types.Contains(c.DocumentType));
        }

        if (filter.Applicability is { } applicability)
        {
            var category = applicability.ProductCategory;
            var model = applicability.ProductModel;
            query = query
                .Where(c => c.ProductCategory == null || c.ProductCategory == category)
                .Where(c => c.ProductModel == null || c.ProductModel == model);

            if (applicability.Region is { } region)
            {
                query = query.Where(c => c.Regions.Length == 0 || c.Regions.Contains(region));
            }
            else
            {
                query = query.Where(c => c.Regions.Length == 0);
            }

            if (applicability.PurchaseDate is { } purchaseDate)
            {
                query = query.Where(c => (c.EffectiveFrom == null || c.EffectiveFrom <= purchaseDate)
                                         && (c.EffectiveTo == null || c.EffectiveTo >= purchaseDate));
            }
        }

        if (filter.DocumentIds is { } documentIds)
        {
            var ids = documentIds.ToArray();
            query = query.Where(c => ids.Contains(c.DocumentId));
        }

        if (filter.ClauseTypes is { } clauseTypes)
        {
            var clauseTypeValues = clauseTypes.Select(t => (ClauseType?)t).ToArray();
            query = query.Where(c => clauseTypeValues.Contains(c.ClauseType));
        }

        return query;
    }

    /// <summary>The tenant a namespace may be written for: none for <c>global</c>, otherwise the tenant in context.</summary>
    private Guid? TenantFor(string knowledgeNamespace)
    {
        if (knowledgeNamespace == KnowledgeDbContext.GlobalNamespace)
        {
            return tenantContext.IsResolved
                ? throw new InvalidOperationException("Global knowledge is written by the platform, never within a tenant context.")
                : null;
        }

        if (!tenantContext.IsResolved || tenantContext.KnowledgeNamespace != knowledgeNamespace)
        {
            throw new InvalidOperationException($"Knowledge namespace '{knowledgeNamespace}' may only be written for its own tenant.");
        }

        return tenantContext.TenantId;
    }

    /// <summary>Platform work on <c>global</c> opens its connections without a tenant session.</summary>
    private IDisposable? PlatformScopeFor(string knowledgeNamespace)
        => knowledgeNamespace == KnowledgeDbContext.GlobalNamespace && !tenantContext.IsResolved ? NoTenantScope.Begin() : null;

    private static void Apply(KnowledgeDocument stored, KnowledgeDocumentDraft document, Guid? tenantId)
    {
        stored.TenantId = tenantId;
        stored.DocumentType = document.DocumentType;
        stored.Title = document.Title;
        stored.Version = document.Version;
        stored.ProductCategory = document.ProductCategory;
        stored.ProductModel = document.ProductModel;
        stored.Regions = [.. document.Regions];
        stored.EffectiveFrom = document.EffectiveFrom;
        stored.EffectiveTo = document.EffectiveTo;
        stored.Classification = document.Classification;
        stored.AllowedRoles = [.. document.AllowedRoles];
        stored.EmbeddingModel = document.EmbeddingModel;
        stored.EmbeddingDim = document.EmbeddingDim;
        stored.Checksum = document.Checksum;
    }

    private static KnowledgeChunk ToChunk(KnowledgeChunkDraft chunk, KnowledgeDocument document) => new()
    {
        Id = Guid.CreateVersion7(),
        Namespace = document.Namespace,
        TenantId = document.TenantId,
        DocumentId = document.Id,
        ChunkIndex = chunk.ChunkIndex,
        ClauseKey = chunk.ClauseKey,
        SectionTitle = chunk.SectionTitle,
        Text = chunk.Text,
        Embedding = new Vector(chunk.Embedding),
        DocumentType = document.DocumentType,
        ProductCategory = document.ProductCategory,
        ProductModel = document.ProductModel,
        Regions = document.Regions,
        EffectiveFrom = document.EffectiveFrom,
        EffectiveTo = document.EffectiveTo,
        Classification = document.Classification,
        AllowedRoles = document.AllowedRoles,
        PolicyVersionId = chunk.PolicyVersionId,
        ClauseType = chunk.ClauseType,
        ExclusionCode = chunk.ExclusionCode,
    };

    internal sealed record DocumentRow(Guid Id, string Namespace, string Title, int Version, Guid? PolicyVersionId);

    internal sealed record ChunkRow(
        Guid Id,
        string Namespace,
        Guid? TenantId,
        Guid DocumentId,
        string Title,
        int Version,
        string? ClauseKey,
        string? SectionTitle,
        string Text,
        DateOnly? EffectiveFrom,
        DateOnly? EffectiveTo,
        double Distance,
        Guid? PolicyVersionId,
        ClauseType? ClauseType,
        ExclusionCode? ExclusionCode);
}
