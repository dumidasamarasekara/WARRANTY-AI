using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Common;
using Warranty.IntegrationTests.Infrastructure;
using Warranty.Knowledge.Retrieval;

namespace Warranty.IntegrationTests.Knowledge;

/// <summary>
/// Version-correct, tenant-isolated policy retrieval against the seeded knowledge index (contracts/rag.md,
/// research R7): the real retriever and pgvector store as the <c>adjudication-service</c> principal of a
/// tenant, with hash embeddings. Temporary test documents use product categories no seeded product has,
/// so they never change what other tests retrieve, and are removed afterwards.
/// </summary>
[Collection(WarrantyAppCollection.Name)]
public sealed class PolicyRetrievalTests(WarrantyAppFixture fixture)
{
    private const string AuroraNamespace = "tenant-aurora";
    private const string BorealisNamespace = "tenant-borealis";

    private static readonly Guid Aurora = TestStaffUsers.AuroraTenantId;
    private static readonly Guid Borealis = TestStaffUsers.BorealisTenantId;
    private static readonly DateOnly BatteryPurchase = new(2026, 3, 1);

    [Fact]
    public async Task A_purchase_on_2026_03_01_at_Aurora_selects_AUR_WP_v1_only()
    {
        var v1 = await AuroraPolicyVersionAsync(1);
        var v2 = await AuroraPolicyVersionAsync(2);

        var result = await RetrieveAsync(Aurora, "aurora", Query("The battery no longer holds a charge", "tablet", Region.EU, BatteryPurchase));

        result.Outcome.ShouldBe(RetrievalOutcome.Ok);
        result.Chunks.ShouldNotBeEmpty();
        result.Chunks.ShouldAllBe(c => c.Namespace == AuroraNamespace && c.TenantId == Aurora);
        result.Chunks.ShouldAllBe(c => c.PolicyVersionId == v1 && c.Version == 1);
        result.Chunks.ShouldNotContain(c => c.PolicyVersionId == v2);
        var battery = result.Chunks.Single(c => c.ClauseKey == "AUR-WP-2.3");
        battery.Text.ShouldContain("6 months");
        battery.Text.ShouldNotContain("12 months");

        // The purchase date, not the query, picks the version: a later purchase gets only v2 (battery 12 months).
        var later = await RetrieveAsync(Aurora, "aurora", Query("The battery no longer holds a charge", "tablet", Region.EU, new DateOnly(2026, 8, 1)));
        later.Outcome.ShouldBe(RetrievalOutcome.Ok);
        later.Chunks.ShouldAllBe(c => c.PolicyVersionId == v2);
        later.Chunks.Single(c => c.ClauseKey == "AUR-WP-2.3").Text.ShouldContain("12 months");
    }

    [Theory]
    [InlineData(Region.EU, true)]
    [InlineData(Region.NA, false)]
    public async Task Region_filtering_selects_only_documents_for_the_purchase_region(Region region, bool euOnlyExpected)
    {
        const string category = "test-region";
        var euOnly = await CopyAuroraV1DocumentAsync("test:region-eu", category, ["EU"]);
        try
        {
            var result = await RetrieveAsync(Aurora, "aurora", Query("Warranty period for a defect", category, region, BatteryPurchase, topK: 50));

            result.Outcome.ShouldBe(RetrievalOutcome.Ok);
            result.Chunks.Any(c => c.DocumentId == euOnly).ShouldBe(euOnlyExpected);
            result.Chunks.ShouldContain(c => c.ClauseKey == "AUR-WP-2.1" && c.DocumentId != euOnly);
        }
        finally
        {
            await DeleteDocumentAsync(euOnly);
        }
    }

    [Fact]
    public async Task Category_filtering_selects_only_documents_for_the_product_category_or_all_categories()
    {
        const string category = "test-category";
        var categoryOnly = await CopyAuroraV1DocumentAsync("test:category", category, []);
        try
        {
            var matching = await RetrieveAsync(Aurora, "aurora", Query("Warranty period for a defect", category, Region.EU, BatteryPurchase, topK: 50));
            matching.Outcome.ShouldBe(RetrievalOutcome.Ok);
            matching.Chunks.ShouldContain(c => c.DocumentId == categoryOnly);

            var tablet = await RetrieveAsync(Aurora, "aurora", Query("Warranty period for a defect", "tablet", Region.EU, BatteryPurchase, topK: 50));
            tablet.Outcome.ShouldBe(RetrievalOutcome.Ok);
            tablet.Chunks.ShouldNotContain(c => c.DocumentId == categoryOnly);

            // No category (a product the catalog does not know) reads only catalog-independent policies.
            var unknown = await RetrieveAsync(Aurora, "aurora", Query("Warranty period for a defect", string.Empty, Region.EU, BatteryPurchase, topK: 50));
            unknown.Outcome.ShouldBe(RetrievalOutcome.Ok);
            unknown.Chunks.ShouldNotContain(c => c.DocumentId == categoryOnly);
            var v1 = await AuroraPolicyVersionAsync(1);
            unknown.Chunks.ShouldAllBe(c => c.PolicyVersionId == v1);
        }
        finally
        {
            await DeleteDocumentAsync(categoryOnly);
        }
    }

    [Fact]
    public async Task A_purchase_before_any_effective_date_has_no_applicable_policy()
    {
        var result = await RetrieveAsync(Aurora, "aurora", Query("The screen stays black", "tablet", Region.EU, new DateOnly(2024, 6, 1)));

        result.Outcome.ShouldBe(RetrievalOutcome.NoApplicablePolicy);
        result.Chunks.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_temporary_overlapping_version_makes_the_policy_ambiguous()
    {
        const string category = "test-ambiguous";
        var v1 = await AuroraPolicyVersionAsync(1);
        var overlapping = Guid.CreateVersion7();
        await using (var owner = await OpenOwnerAsync())
        {
            await ExecuteAsync(
                owner,
                "insert into policy.policy_versions " +
                "(id, tenant_id, policy_id, version, effective_from, effective_to, regions, product_categories, terms, source_blob_path, checksum) " +
                "select @id, tenant_id, policy_id, 99, date '2026-02-01', date '2026-04-30', regions, array[@category], terms, source_blob_path, checksum " +
                "from policy.policy_versions where id = @v1",
                ("id", overlapping), ("category", category), ("v1", v1));
        }

        var document = await CopyAuroraV1DocumentAsync(
            "test:ambiguous", category, [], overlapping, new DateOnly(2026, 2, 1), new DateOnly(2026, 4, 30), version: 99);
        try
        {
            var ambiguous = await RetrieveAsync(Aurora, "aurora", Query("The battery no longer holds a charge", category, Region.EU, BatteryPurchase));
            ambiguous.Outcome.ShouldBe(RetrievalOutcome.AmbiguousPolicyVersion);
            ambiguous.Chunks.ShouldBeEmpty();

            // Outside the overlap, or for another category, exactly one version applies again.
            (await RetrieveAsync(Aurora, "aurora", Query("The battery no longer holds a charge", category, Region.EU, new DateOnly(2026, 5, 15))))
                .Outcome.ShouldBe(RetrievalOutcome.Ok);
            (await RetrieveAsync(Aurora, "aurora", Query("The battery no longer holds a charge", "tablet", Region.EU, BatteryPurchase)))
                .Outcome.ShouldBe(RetrievalOutcome.Ok);
        }
        finally
        {
            await DeleteDocumentAsync(document);
            await using var owner = await OpenOwnerAsync();
            await ExecuteAsync(owner, "delete from policy.policy_versions where id = @id", ("id", overlapping));
        }
    }

    [Fact]
    public async Task Aurora_never_retrieves_Borealis_chunks_even_for_identical_query_text()
    {
        await using var knowledgeOwner = await OpenOwnerAsync("knowledge");
        var borealisText = await ScalarAsync<string>(
            knowledgeOwner,
            "select text from knowledge.knowledge_chunks where namespace = @namespace and clause_key = 'BOR-WP-1.2'",
            ("namespace", BorealisNamespace));

        // The same text finds its own clause at Borealis, so the Aurora checks below are not vacuous.
        var atBorealis = await RetrieveAsync(Borealis, "borealis", Query(borealisText, "tablet", Region.EU, BatteryPurchase));
        atBorealis.Outcome.ShouldBe(RetrievalOutcome.Ok);
        atBorealis.Chunks.ShouldContain(c => c.ClauseKey == "BOR-WP-1.2");

        var policy = await RetrieveAsync(Aurora, "aurora", Query(borealisText, "tablet", Region.EU, BatteryPurchase, topK: 50));
        policy.Outcome.ShouldBe(RetrievalOutcome.Ok);
        policy.Chunks.ShouldNotBeEmpty();
        AssertOnlyAurora(policy);

        foreach (var scope in new[] { KnowledgeScope.Tenant, KnowledgeScope.GlobalAndTenant })
        {
            var search = await RunAsAsync(Aurora, "aurora", (retriever, ct) => retriever.SearchAsync(
                new KnowledgeSearchQuery(borealisText, scope, null, null, TopK: 50), ct));
            search.Outcome.ShouldBe(RetrievalOutcome.Ok);
            search.Chunks.ShouldNotBeEmpty();
            AssertOnlyAurora(search);
        }
    }

    [Fact]
    public async Task A_forged_chunk_from_another_namespace_fails_closed_with_a_security_event()
    {
        await using var knowledgeOwner = await OpenOwnerAsync("knowledge");
        await using var command = Command(
            knowledgeOwner,
            "select id, document_id, clause_key, text from knowledge.knowledge_chunks where namespace = @namespace order by clause_key limit 1",
            [("namespace", BorealisNamespace)]);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        var forged = new RetrievedChunk(
            reader.GetGuid(0), BorealisNamespace, reader.GetGuid(1), "Borealis Care Warranty", 1, reader.GetString(2), reader.GetString(2),
            reader.GetString(3), null, null, 0.99, Borealis);
        await reader.CloseAsync();

        var runId = Guid.CreateVersion7();
        var result = await RunInScopeAsync(Aurora, "aurora", (services, ct) =>
        {
            // The real store, with a chunk of another namespace slipped into every ranked result.
            var retriever = new KnowledgeRetriever(
                new ForgingStore(services.GetRequiredService<IKnowledgeStore>(), forged),
                services.GetRequiredService<IPolicyRepository>(),
                services.GetRequiredService<IAiGateway>(),
                services.GetRequiredService<ITenantContext>(),
                services.GetRequiredService<IAiOpsRepository>(),
                services.GetRequiredService<ISecurityEventWriter>(),
                services.GetRequiredService<IPiiRedactor>(),
                TimeProvider.System);
            return retriever.RetrievePolicyClausesAsync(
                Query("The battery no longer holds a charge", "tablet", Region.EU, BatteryPurchase) with
                {
                    Attribution = new RetrievalAttribution("policy", runId),
                },
                ct);
        });

        result.Outcome.ShouldBe(RetrievalOutcome.ScopeViolation);
        result.Chunks.ShouldBeEmpty();
        await AssertScopeViolationEventAsync($"run:{runId}", forged.ChunkId);
    }

    [Fact]
    public async Task A_forged_row_of_another_tenant_in_the_tenant_partition_fails_closed_with_a_security_event()
    {
        // A corrupted index row: Aurora's namespace, Borealis's tenant. RLS filters by namespace only, so
        // the post-retrieval assertion is what stops it.
        var v1Document = await AuroraV1DocumentAsync();
        var forgedId = Guid.CreateVersion7();
        await using (var owner = await OpenOwnerAsync("knowledge"))
        {
            await ExecuteAsync(
                owner,
                "insert into knowledge.knowledge_chunks " +
                "(id, namespace, tenant_id, document_id, chunk_index, clause_key, section_title, text, embedding, document_type, " +
                " product_category, product_model, regions, effective_from, effective_to, classification, allowed_roles, " +
                " policy_version_id, clause_type, exclusion_code) " +
                "select @forged, namespace, @borealis, document_id, 9999, clause_key, section_title, text, embedding, document_type, " +
                " product_category, product_model, regions, effective_from, effective_to, classification, allowed_roles, " +
                " policy_version_id, clause_type, exclusion_code " +
                "from knowledge.knowledge_chunks where namespace = @namespace and document_id = @document and clause_key = 'AUR-WP-2.3'",
                ("forged", forgedId), ("namespace", AuroraNamespace), ("document", v1Document));
        }

        try
        {
            var runId = Guid.CreateVersion7();
            var result = await RetrieveAsync(
                Aurora,
                "aurora",
                Query("The battery no longer holds a charge", "tablet", Region.EU, BatteryPurchase) with
                {
                    Attribution = new RetrievalAttribution("policy", runId),
                });

            result.Outcome.ShouldBe(RetrievalOutcome.ScopeViolation);
            result.Chunks.ShouldBeEmpty();
            await AssertScopeViolationEventAsync($"run:{runId}", forgedId);
        }
        finally
        {
            await using var owner = await OpenOwnerAsync("knowledge");
            await ExecuteAsync(owner, "delete from knowledge.knowledge_chunks where namespace = @namespace and id = @id",
                ("namespace", AuroraNamespace), ("id", forgedId));
        }
    }

    private static PolicyRetrievalQuery Query(string text, string category, Region region, DateOnly purchaseDate, int topK = 8)
        => new(text, category, null, region, purchaseDate, topK, new RetrievalAttribution("policy"));

    private static void AssertOnlyAurora(RetrievalResult result)
    {
        result.Chunks.ShouldAllBe(c => c.Namespace == AuroraNamespace || c.Namespace == NamespaceGuard.GlobalNamespace);
        result.Chunks.ShouldNotContain(c => c.TenantId == Borealis);
        result.Chunks.ShouldNotContain(c => c.ClauseKey != null && c.ClauseKey.StartsWith("BOR-", StringComparison.Ordinal));
    }

    private Task<RetrievalResult> RetrieveAsync(Guid tenantId, string tenantSlug, PolicyRetrievalQuery query)
        => RunAsAsync(tenantId, tenantSlug, (retriever, ct) => retriever.RetrievePolicyClausesAsync(query, ct));

    private Task<RetrievalResult> RunAsAsync(
        Guid tenantId, string tenantSlug, Func<IKnowledgeRetriever, CancellationToken, Task<RetrievalResult>> retrieve)
        => RunInScopeAsync(tenantId, tenantSlug, (services, ct) => retrieve(services.GetRequiredService<IKnowledgeRetriever>(), ct));

    /// <summary>Runs in a fresh DI scope under a tenant scope for the <c>adjudication-service</c> principal, as the claim job worker does.</summary>
    private async Task<RetrievalResult> RunInScopeAsync(
        Guid tenantId, string tenantSlug, Func<IServiceProvider, CancellationToken, Task<RetrievalResult>> retrieve)
    {
        using var tenant = TenantContextScope.Begin(tenantId, tenantSlug);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await retrieve(scope.ServiceProvider, TestContext.Current.CancellationToken);
    }

    private async Task AssertScopeViolationEventAsync(string target, Guid offendingChunkId)
    {
        await using var owner = await OpenOwnerAsync();
        await using var command = Command(
            owner,
            "select tenant_id, actor, details::text from audit.security_events where kind = @kind and target = @target",
            [("kind", WireName.Of(SecurityEventKind.RetrievalScopeViolation)), ("target", target)]);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue($"no RETRIEVAL_SCOPE_VIOLATION event for {target}");
        reader.GetGuid(0).ShouldBe(Aurora);
        reader.GetString(1).ShouldBe(Principals.AdjudicationService);
        reader.GetString(2).ShouldContain(offendingChunkId.ToString());
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    private async Task<Guid> AuroraPolicyVersionAsync(int version)
    {
        await using var owner = await OpenOwnerAsync();
        return await ScalarAsync<Guid>(
            owner,
            "select v.id from policy.policy_versions v join policy.warranty_policies p on p.tenant_id = v.tenant_id and p.id = v.policy_id " +
            "where p.tenant_id = @aurora and p.code = 'AUR-WP' and v.version = @version",
            ("version", version));
    }

    /// <summary>The indexed document of AUR-WP v1 (the ingestor's source ref is <c>policy_version:{id}</c>).</summary>
    private async Task<Guid> AuroraV1DocumentAsync()
    {
        var v1 = await AuroraPolicyVersionAsync(1);
        await using var owner = await OpenOwnerAsync("knowledge");
        return await ScalarAsync<Guid>(
            owner,
            "select id from knowledge.knowledge_documents where namespace = @namespace and source_ref = @source",
            ("namespace", AuroraNamespace), ("source", $"policy_version:{v1}"));
    }

    /// <summary>
    /// Indexes a copy of the AUR-WP v1 document under other filters. Without <paramref name="policyVersionId"/>
    /// its chunks belong to no policy version, so it is a separate policy for version selection.
    /// </summary>
    private async Task<Guid> CopyAuroraV1DocumentAsync(
        string sourceRef, string category, string[] regions, Guid? policyVersionId = null, DateOnly? effectiveFrom = null,
        DateOnly? effectiveTo = null, int version = 1)
    {
        var source = await AuroraV1DocumentAsync();
        var id = Guid.CreateVersion7();
        await using var owner = await OpenOwnerAsync("knowledge");
        await using var transaction = await owner.BeginTransactionAsync(TestContext.Current.CancellationToken);
        (string, object)[] parameters =
        [
            ("id", id), ("source", source), ("sourceRef", sourceRef), ("category", category), ("regions", regions), ("version", version),
            ("versionId", (object?)policyVersionId ?? DBNull.Value),
            ("from", (object?)effectiveFrom ?? DBNull.Value), ("to", (object?)effectiveTo ?? DBNull.Value),
        ];
        await ExecuteAsync(
            owner,
            "insert into knowledge.knowledge_documents " +
            "(id, namespace, tenant_id, document_type, source_ref, title, version, product_category, product_model, regions, " +
            " effective_from, effective_to, classification, allowed_roles, embedding_model, embedding_dim, checksum) " +
            "select @id, namespace, tenant_id, document_type, @sourceRef, title, @version, @category, product_model, @regions, " +
            " coalesce(@from::date, effective_from), coalesce(@to::date, effective_to), classification, allowed_roles, " +
            " embedding_model, embedding_dim, checksum " +
            "from knowledge.knowledge_documents where id = @source",
            parameters);
        await ExecuteAsync(
            owner,
            "insert into knowledge.knowledge_chunks " +
            "(id, namespace, tenant_id, document_id, chunk_index, clause_key, section_title, text, embedding, document_type, " +
            " product_category, product_model, regions, effective_from, effective_to, classification, allowed_roles, " +
            " policy_version_id, clause_type, exclusion_code) " +
            "select gen_random_uuid(), namespace, tenant_id, @id, chunk_index, clause_key, section_title, text, embedding, document_type, " +
            " @category, product_model, @regions, coalesce(@from::date, effective_from), coalesce(@to::date, effective_to), " +
            " classification, allowed_roles, @versionId::uuid, clause_type, exclusion_code " +
            "from knowledge.knowledge_chunks where namespace = @namespace and document_id = @source",
            [.. parameters, ("namespace", AuroraNamespace)]);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task DeleteDocumentAsync(Guid documentId)
    {
        await using var owner = await OpenOwnerAsync("knowledge");
        await ExecuteAsync(owner, "delete from knowledge.knowledge_chunks where namespace = @namespace and document_id = @id",
            ("namespace", AuroraNamespace), ("id", documentId));
        await ExecuteAsync(owner, "delete from knowledge.knowledge_documents where id = @id", ("id", documentId));
    }

    /// <summary>The database owner (not subject to row-level security), for setting up and inspecting rows.</summary>
    private async Task<NpgsqlConnection> OpenOwnerAsync(string database = "warranty")
    {
        var connection = new NpgsqlConnection(fixture.OwnerConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("aurora", Aurora);
        command.Parameters.AddWithValue("borealis", Borealis);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Delegates to the real store and appends a forged chunk to every ranked result.</summary>
    private sealed class ForgingStore(IKnowledgeStore inner, RetrievedChunk forged) : IKnowledgeStore
    {
        public Task<StoredKnowledgeDocument?> FindDocumentAsync(string knowledgeNamespace, string sourceRef, CancellationToken ct)
            => inner.FindDocumentAsync(knowledgeNamespace, sourceRef, ct);

        public Task<Guid> SaveDocumentAsync(KnowledgeDocumentDraft document, IReadOnlyList<KnowledgeChunkDraft> chunks, CancellationToken ct)
            => inner.SaveDocumentAsync(document, chunks, ct);

        public Task<IReadOnlyList<KnowledgeDocumentMatch>> FindDocumentsAsync(KnowledgeFilter filter, CancellationToken ct)
            => inner.FindDocumentsAsync(filter, ct);

        public async Task<IReadOnlyList<RetrievedChunk>> SearchChunksAsync(
            KnowledgeFilter filter, ReadOnlyMemory<float> embedding, int topK, CancellationToken ct)
            => [.. await inner.SearchChunksAsync(filter, embedding, topK, ct), forged];
    }
}
