using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Pgvector;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Domain.Common;
using Warranty.Domain.Policies;
using Warranty.Infrastructure.Persistence.Knowledge;
using Warranty.Infrastructure.Persistence.Sql;

namespace Warranty.UnitTests.Infrastructure;

/// <summary>Checks the knowledge EF model and its SQL script without a database connection.</summary>
public sealed class KnowledgeModelTests
{
    private static readonly Guid Aurora = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private static KnowledgeDbContext CreateContext(ITenantContext tenant)
        => new(
            new DbContextOptionsBuilder<KnowledgeDbContext>()
                .UseNpgsql("Host=localhost;Database=knowledge;Username=unused", o => o.UseVector())
                .Options,
            tenant);

    [Fact]
    public void Tables_map_to_the_sql_owned_knowledge_schema_and_are_excluded_from_migrations()
    {
        using var context = CreateContext(new FakeTenantContext(Aurora));
        var designTimeModel = context.GetService<IDesignTimeModel>().Model;

        foreach (var (type, table) in new[] { (typeof(KnowledgeDocument), "knowledge_documents"), (typeof(KnowledgeChunk), "knowledge_chunks") })
        {
            var entity = designTimeModel.FindEntityType(type)!;
            entity.GetSchema().ShouldBe("knowledge");
            entity.GetTableName().ShouldBe(table);
            entity.IsTableExcludedFromMigrations().ShouldBeTrue();
        }

        var chunk = context.Model.FindEntityType(typeof(KnowledgeChunk))!;
        chunk.FindProperty(nameof(KnowledgeChunk.Embedding))!.GetColumnType().ShouldBe("vector(768)");
        chunk.FindProperty(nameof(KnowledgeChunk.ExclusionCode))!.GetColumnName().ShouldBe("exclusion_code");
        chunk.FindPrimaryKey()!.Properties.Select(p => p.Name).ShouldBe([nameof(KnowledgeChunk.Namespace), nameof(KnowledgeChunk.Id)]);
    }

    [Fact]
    public void Queries_are_limited_to_global_and_the_current_tenant_namespace()
    {
        using var context = CreateContext(new FakeTenantContext(Aurora));

        var sql = context.Chunks.Where(c => c.DocumentType == DocumentType.WarrantyPolicy).ToQueryString();

        sql.ShouldContain("FROM knowledge.knowledge_chunks");
        sql.ShouldContain("'global'");
        sql.ShouldContain("tenant-aurora");
        sql.ShouldContain("'WarrantyPolicy'");
    }

    [Fact]
    public void Without_a_resolved_tenant_only_global_knowledge_matches()
    {
        using var context = CreateContext(new FakeTenantContext(null));

        context.Documents.ToQueryString().ShouldNotContain("tenant-");
    }

    [Fact]
    public void Retrieval_applies_every_hard_filter_in_sql_before_cosine_ranking()
    {
        var tenant = new FakeTenantContext(Aurora);
        using var context = CreateContext(tenant);
        var store = new KnowledgeStore(context, tenant);
        var filter = new KnowledgeFilter(
            ["tenant-aurora"],
            [DocumentType.WarrantyPolicy],
            new PolicyApplicability("tablet", "AUR-TAB10", Region.EU, new DateOnly(2026, 3, 1)),
            [DocumentClassification.Public, DocumentClassification.Internal],
            ["adjudication-service"],
            [Guid.NewGuid()],
            [ClauseType.Period, ClauseType.Exclusion]);

        var sql = store.ChunksQuery(filter, new Vector(new float[KnowledgeDbContext.EmbeddingDimensions]), 8).ToQueryString();

        sql.ShouldContain("k.namespace = ANY (@namespaces)");
        sql.ShouldContain("k.classification = ANY (@classifications)");
        sql.ShouldContain("k.allowed_roles && @roles");
        sql.ShouldContain("k.document_type = ANY (@types)");
        sql.ShouldContain("k.product_category IS NULL OR k.product_category = @category");
        sql.ShouldContain("k.product_model IS NULL OR k.product_model = @model");
        sql.ShouldContain("cardinality(k.regions) = 0 OR @region = ANY (k.regions)");
        sql.ShouldContain("k.effective_from <= @purchaseDate");
        sql.ShouldContain("k.effective_to >= @purchaseDate");
        sql.ShouldContain("k.document_id = ANY (@ids)");
        sql.ShouldContain("k.clause_type = ANY (@clauseTypeValues)");
        sql.ShouldContain("ORDER BY k.embedding <=> @embedding");
        sql.ShouldContain("LIMIT @p");
        sql.IndexOf("WHERE", StringComparison.Ordinal).ShouldBeLessThan(sql.IndexOf("ORDER BY", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_category_model_and_region_match_only_documents_for_all()
    {
        var tenant = new FakeTenantContext(Aurora);
        using var context = CreateContext(tenant);
        var store = new KnowledgeStore(context, tenant);
        var filter = new KnowledgeFilter(
            ["global"], null, new PolicyApplicability(null, null, null, null), [DocumentClassification.Public], ["adjudication-service"]);

        var sql = store.DocumentsQuery(filter).ToQueryString();

        sql.ShouldContain("SELECT DISTINCT");
        sql.ShouldContain("cardinality(k.regions) = 0");
        sql.ShouldNotContain("ANY (k.regions)");
        sql.ShouldNotContain("effective_from");
        sql.ShouldNotContain("document_type = ANY");
    }

    [Fact]
    public void Each_sql_script_targets_its_database()
    {
        SqlScripts.Load(SqlDatabase.Warranty).Select(s => s.Name).ShouldContain("001_roles_and_rls.sql");
        SqlScripts.Load(SqlDatabase.Warranty).Select(s => s.Name).ShouldNotContain("002_knowledge.sql");

        var knowledge = SqlScripts.Load(SqlDatabase.Knowledge).ShouldHaveSingleItem();
        knowledge.Name.ShouldBe("002_knowledge.sql");
        knowledge.Sql.ShouldContain("PARTITION BY LIST (namespace)");
        knowledge.Sql.ShouldContain("knowledge.ensure_namespace(ns text)");
        knowledge.Sql.ShouldContain("current_setting('app.kb_namespace', true)");
    }
}
