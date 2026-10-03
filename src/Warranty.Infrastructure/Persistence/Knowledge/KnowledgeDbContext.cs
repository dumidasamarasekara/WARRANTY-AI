using Microsoft.EntityFrameworkCore;
using Warranty.Application.Abstractions;

namespace Warranty.Infrastructure.Persistence.Knowledge;

/// <summary>
/// Knowledge database <c>knowledge</c>. The schema is created by <c>Sql/002_knowledge.sql</c>, not by
/// migrations (the chunk table is partitioned by namespace with an HNSW index per partition), so
/// both tables are excluded from migrations. The Npgsql options must call <c>UseVector()</c>.
/// A query filter limits reads to <c>global</c> and the current tenant's namespace; RLS on
/// <c>app.kb_namespace</c> enforces the same rule independently (research R7).
/// </summary>
public sealed class KnowledgeDbContext(DbContextOptions<KnowledgeDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string GlobalNamespace = "global";

    public const int EmbeddingDimensions = 768;

    /// <summary>The tenant namespace used by the query filters; none when no tenant is resolved.</summary>
    internal string CurrentNamespace => tenantContext.IsResolved ? tenantContext.KnowledgeNamespace : string.Empty;

    public DbSet<KnowledgeDocument> Documents => Set<KnowledgeDocument>();

    public DbSet<KnowledgeChunk> Chunks => Set<KnowledgeChunk>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KnowledgeDocument>(builder =>
        {
            builder.ToTable("knowledge_documents", "knowledge", t => t.ExcludeFromMigrations());
            builder.HasKey(d => d.Id);
            builder.Property(d => d.Id).ValueGeneratedNever();
            builder.Property(d => d.DocumentType).HasWireName();
            builder.Property(d => d.Classification).HasWireName();
            builder.PrimitiveCollection(d => d.Regions).ElementType(e => e.HasConversion<string>());
            builder.HasIndex(d => new { d.Namespace, d.SourceRef }).IsUnique();
            builder.HasQueryFilter(d => d.Namespace == GlobalNamespace || d.Namespace == CurrentNamespace);
        });

        modelBuilder.Entity<KnowledgeChunk>(builder =>
        {
            builder.ToTable("knowledge_chunks", "knowledge", t => t.ExcludeFromMigrations());
            builder.HasKey(c => new { c.Namespace, c.Id });
            builder.Property(c => c.Embedding).HasColumnType($"vector({EmbeddingDimensions})");
            builder.Property(c => c.DocumentType).HasWireName();
            builder.Property(c => c.Classification).HasWireName();
            builder.Property(c => c.ClauseType).HasWireName();
            builder.Property(c => c.ExclusionCode).HasWireName();
            builder.PrimitiveCollection(c => c.Regions).ElementType(e => e.HasConversion<string>());
            builder.HasOne<KnowledgeDocument>().WithMany().HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);
            builder.HasQueryFilter(c => c.Namespace == GlobalNamespace || c.Namespace == CurrentNamespace);
        });

        modelBuilder.ApplySnakeCaseNames();
    }
}
