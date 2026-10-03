using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.Knowledge;
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
