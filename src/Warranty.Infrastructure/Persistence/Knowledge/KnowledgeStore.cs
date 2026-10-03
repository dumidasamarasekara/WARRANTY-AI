using Microsoft.EntityFrameworkCore;
using Pgvector;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Knowledge;

namespace Warranty.Infrastructure.Persistence.Knowledge;

/// <summary>
/// The pgvector <see cref="IKnowledgeStore"/> (research R6). A tenant-namespace document is written
/// only for the tenant in context, and a <c>global</c> one only without a tenant context — then the
/// connection runs in a <see cref="NoTenantScope"/>, so no tenant namespace is visible to it. Writes
/// need the knowledge database owner (<c>warranty_app</c> may only read), which only the migration
/// service connects as (research R8).
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
}
