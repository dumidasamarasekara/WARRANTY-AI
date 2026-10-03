using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.AiOps;
using Warranty.Domain.Common;
using Warranty.Domain.Policies;
using Warranty.Knowledge.Retrieval;

namespace Warranty.UnitTests.Knowledge;

public sealed class KnowledgeRetrievalTests
{
    private static readonly Guid Aurora = Guid.Parse("0199a000-0000-7000-8000-000000000001");
    private static readonly Guid Borealis = Guid.Parse("0199a000-0000-7000-8000-000000000002");
    private static readonly Guid RunId = Guid.Parse("0199a000-0000-7000-8000-0000000000aa");

    private readonly FakeStore _store = new();
    private readonly IPolicyRepository _policies = Substitute.For<IPolicyRepository>();
    private readonly IAiGateway _gateway = Substitute.For<IAiGateway>();
    private readonly IAiOpsRepository _aiOps = Substitute.For<IAiOpsRepository>();
    private readonly ISecurityEventWriter _securityEvents = Substitute.For<ISecurityEventWriter>();
    private readonly List<RagQuery> _ragQueries = [];
    private readonly List<PolicyVersion> _versions = [];

    public KnowledgeRetrievalTests()
    {
        _gateway.EmbedAsync(Arg.Any<AiEmbeddingRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AiEmbeddingResult([new float[768]], "nomic-embed-text", 768, AiUsage.None("ollama", "nomic-embed-text")));
        _aiOps.When(a => a.AddRagQuery(Arg.Any<RagQuery>())).Do(call => _ragQueries.Add(call.Arg<RagQuery>()));
        _policies.GetVersionsAsync(Arg.Any<CancellationToken>()).Returns(_ => (IReadOnlyList<PolicyVersion>)_versions);
    }

    private static PolicyRetrievalQuery PolicyQuery => new(
        "Screen flickers, contact me at jane@example.com", "tablet", "AUR-TAB10", Region.EU, new DateOnly(2026, 3, 1),
        TopK: 2, Attribution: new RetrievalAttribution("policy", RunId));

    [Fact]
    public async Task Policy_retrieval_filters_by_the_tenant_context_and_adds_decisive_clauses()
    {
        var version = AddVersion(Guid.NewGuid());
        var document = _store.AddDocument("tenant-aurora", version.Id);
        var coverage = Chunk(document, "AUR-WP-1", ClauseType.Coverage, 0.9);
        var period = Chunk(document, "AUR-WP-2.1", ClauseType.Period, 0.5);
        var exclusion = Chunk(document, "AUR-WP-3.2", ClauseType.Exclusion, 0.4);
        _store.Ranked.AddRange([coverage, period]);
        _store.Decisive.AddRange([period, exclusion]);

        var result = await Retriever(SystemContext()).RetrievePolicyClausesAsync(PolicyQuery, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(RetrievalOutcome.Ok);
        result.Chunks.Select(c => c.ClauseKey).ShouldBe(["AUR-WP-1", "AUR-WP-2.1", "AUR-WP-3.2"]);

        var filter = _store.DocumentFilters.ShouldHaveSingleItem();
        filter.Namespaces.ShouldBe(["tenant-aurora"]);
        filter.DocumentTypes.ShouldBe([DocumentType.WarrantyPolicy]);
        filter.Applicability.ShouldBe(new PolicyApplicability("tablet", "AUR-TAB10", Region.EU, new DateOnly(2026, 3, 1)));
        filter.Roles.ShouldBe([Principals.AdjudicationService]);
        filter.Classifications.ShouldBe([DocumentClassification.Public, DocumentClassification.Internal, DocumentClassification.Confidential]);

        _store.Searches.Select(s => (s.Filter.DocumentIds!.Single(), s.TopK)).ShouldBe([(document.DocumentId, 2), (document.DocumentId, KnowledgeRetriever.MaxTopK)]);
        _store.Searches[0].Filter.ClauseTypes.ShouldBeNull();
        _store.Searches[1].Filter.ClauseTypes.ShouldBe([ClauseType.Period, ClauseType.Exclusion]);
    }

    [Fact]
    public async Task Each_call_records_one_redacted_rag_query()
    {
        var version = AddVersion(Guid.NewGuid());
        var document = _store.AddDocument("tenant-aurora", version.Id);
        _store.Ranked.Add(Chunk(document, "AUR-WP-1", ClauseType.Coverage, 0.75));

        await Retriever(SystemContext()).RetrievePolicyClausesAsync(PolicyQuery, TestContext.Current.CancellationToken);

        var row = _ragQueries.ShouldHaveSingleItem();
        row.TenantId.ShouldBe(Aurora);
        row.RunId.ShouldBe(RunId);
        row.Agent.ShouldBe("policy");
        row.Namespaces.ShouldBe(["tenant-aurora"]);
        row.QueryText.ShouldBe("Screen flickers, contact me at [EMAIL]");
        row.TopK.ShouldBe(2);
        row.FiltersJson.ShouldContain("\"purchaseDate\":\"2026-03-01\"");
        row.FiltersJson.ShouldContain("\"region\":\"EU\"");
        using var results = JsonDocument.Parse(row.ResultsJson);
        results.RootElement.EnumerateArray().Select(r => r.GetProperty("clauseKey").GetString()).ShouldBe(["AUR-WP-1"]);

        var embedded = (AiEmbeddingRequest)_gateway.ReceivedCalls().Single().GetArguments()[0]!;
        embedded.Inputs.ShouldBe(["Screen flickers, contact me at [EMAIL]"]);
        embedded.Context.ShouldBe(new AiCallContext(Aurora, null, RunId, "policy", "corr-1"));
    }

    [Fact]
    public async Task No_applicable_version_returns_no_policy_without_embedding()
    {
        var result = await Retriever(SystemContext()).RetrievePolicyClausesAsync(PolicyQuery, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(RetrievalOutcome.NoApplicablePolicy);
        result.Chunks.ShouldBeEmpty();
        await _gateway.DidNotReceiveWithAnyArgs().EmbedAsync(default!, TestContext.Current.CancellationToken);
        _ragQueries.ShouldHaveSingleItem().ResultsJson.ShouldBe("[]");
    }

    [Fact]
    public async Task Two_versions_of_one_policy_are_ambiguous()
    {
        var policyId = Guid.NewGuid();
        _store.AddDocument("tenant-aurora", AddVersion(policyId, 1).Id);
        _store.AddDocument("tenant-aurora", AddVersion(policyId, 2).Id);

        var result = await Retriever(SystemContext()).RetrievePolicyClausesAsync(PolicyQuery, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(RetrievalOutcome.AmbiguousPolicyVersion);
        result.Chunks.ShouldBeEmpty();
        _store.Searches.ShouldBeEmpty();
    }

    [Fact]
    public async Task One_version_of_each_of_two_policies_is_not_ambiguous()
    {
        _store.AddDocument("tenant-aurora", AddVersion(Guid.NewGuid()).Id);
        _store.AddDocument("tenant-aurora", AddVersion(Guid.NewGuid()).Id);

        var result = await Retriever(SystemContext()).RetrievePolicyClausesAsync(PolicyQuery, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(RetrievalOutcome.Ok);
        _store.Searches[0].Filter.DocumentIds!.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("tenant-borealis", "borealis")]
    [InlineData("tenant-aurora", "borealis")]
    [InlineData("global", "aurora")]
    public async Task A_chunk_outside_the_context_fails_closed_with_a_security_event(string chunkNamespace, string chunkTenant)
    {
        var version = AddVersion(Guid.NewGuid());
        var document = _store.AddDocument("tenant-aurora", version.Id);
        _store.Ranked.Add(Chunk(document, "AUR-WP-1", ClauseType.Coverage, 0.9));
        _store.Ranked.Add(Chunk(document, "BOR-WP-1", ClauseType.Coverage, 0.8) with
        {
            Namespace = chunkNamespace,
            TenantId = chunkTenant == "aurora" ? Aurora : Borealis,
        });

        var result = await Retriever(SystemContext()).RetrievePolicyClausesAsync(PolicyQuery, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(RetrievalOutcome.ScopeViolation);
        result.Chunks.ShouldBeEmpty();
        await _securityEvents.Received(1).RecordAsync(
            SecurityEventKind.RetrievalScopeViolation, Principals.AdjudicationService, $"run:{RunId}", Arg.Any<object>(), Arg.Any<CancellationToken>());
        _ragQueries.ShouldHaveSingleItem().ResultsJson.ShouldBe("[]");
        _ragQueries[0].FiltersJson.ShouldContain("\"outcome\":\"ScopeViolation\"");
    }

    [Fact]
    public async Task A_document_from_another_namespace_fails_closed_before_ranking()
    {
        _store.AddDocument("tenant-borealis", AddVersion(Guid.NewGuid()).Id);

        var result = await Retriever(SystemContext()).RetrievePolicyClausesAsync(PolicyQuery, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(RetrievalOutcome.ScopeViolation);
        _store.Searches.ShouldBeEmpty();
        await _securityEvents.ReceivedWithAnyArgs(1).RecordAsync(default, default!, default, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(KnowledgeScope.Global, new[] { "global" })]
    [InlineData(KnowledgeScope.Tenant, new[] { "tenant-aurora" })]
    [InlineData(KnowledgeScope.GlobalAndTenant, new[] { "tenant-aurora", "global" })]
    public async Task Search_reads_only_the_namespaces_of_its_scope(KnowledgeScope scope, string[] namespaces)
    {
        var global = new KnowledgeDocumentMatch(Guid.NewGuid(), "global", "Terminology", 1, null);
        _store.Ranked.Add(Chunk(global, null, null, 0.6) with { TenantId = null });

        var result = await Retriever(SystemContext()).SearchAsync(
            new KnowledgeSearchQuery("water damage", scope, [DocumentType.Terminology], null), TestContext.Current.CancellationToken);

        var search = _store.Searches.ShouldHaveSingleItem();
        search.Filter.Namespaces.ShouldBe(namespaces);
        search.Filter.DocumentTypes.ShouldBe([DocumentType.Terminology]);
        search.Filter.Applicability.ShouldBeNull();
        search.TopK.ShouldBe(5);
        result.Outcome.ShouldBe(scope == KnowledgeScope.Tenant ? RetrievalOutcome.ScopeViolation : RetrievalOutcome.Ok);
        _ragQueries.ShouldHaveSingleItem().Agent.ShouldBe(Principals.AdjudicationService);
    }

    [Fact]
    public async Task Retrieval_without_a_tenant_context_throws()
    {
        var retriever = Retriever(new TestTenant { IsResolved = false });

        await Should.ThrowAsync<InvalidOperationException>(() => retriever.RetrievePolicyClausesAsync(PolicyQuery, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<InvalidOperationException>(() => retriever.SearchAsync(
            new KnowledgeSearchQuery("x", KnowledgeScope.Global, null, null), TestContext.Current.CancellationToken));
        _ragQueries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(KnowledgeRetriever.MaxTopK + 1)]
    public async Task Top_k_must_be_within_bounds(int topK)
        => await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Retriever(SystemContext())
            .RetrievePolicyClausesAsync(PolicyQuery with { TopK = topK }, TestContext.Current.CancellationToken));

    [Fact]
    public void Classifications_follow_the_principal()
    {
        NamespaceGuard.ClassificationsFor(SystemContext()).ShouldContain(DocumentClassification.Confidential);
        NamespaceGuard.ClassificationsFor(new TestTenant { PrincipalId = "u1", Roles = new HashSet<string> { Principals.ClaimsAgentRole } })
            .ShouldBe([DocumentClassification.Public, DocumentClassification.Internal]);
        NamespaceGuard.ClassificationsFor(new TestTenant { PrincipalId = "u2", Roles = new HashSet<string> { Principals.AuditorRole } })
            .ShouldContain(DocumentClassification.Confidential);
        NamespaceGuard.ClassificationsFor(new TestTenant { PrincipalId = $"claimant:{Guid.NewGuid()}" })
            .ShouldBe([DocumentClassification.Public]);
    }

    private static TestTenant SystemContext() => new()
    {
        PrincipalId = Principals.AdjudicationService,
        Roles = new HashSet<string> { Principals.AdjudicationService },
        IsSystem = true,
    };

    private KnowledgeRetriever Retriever(ITenantContext tenant)
        => new(_store, _policies, _gateway, tenant, _aiOps, _securityEvents, new EmailRedactor(), new FakeTimeProvider());

    private PolicyVersion AddVersion(Guid policyId, int number = 1)
    {
        var version = PolicyVersion.Create(
            Guid.NewGuid(), Aurora, policyId, number, new DateOnly(2025, 1, 1), null, [Region.EU], [],
            new CoverageTerms(new Dictionary<Region, int> { [Region.EU] = 24 }, new Dictionary<string, int>(), AccidentalDamageTerms.NotCovered, []),
            "p.md", "abc");
        _versions.Add(version);
        return version;
    }

    private static RetrievedChunk Chunk(KnowledgeDocumentMatch document, string? clauseKey, ClauseType? type, double score)
        => new(
            Guid.NewGuid(), document.Namespace, document.DocumentId, document.Title, document.Version, clauseKey, clauseKey, "text",
            new DateOnly(2025, 1, 1), null, score, Aurora, document.PolicyVersionId, type);

    private sealed class FakeStore : IKnowledgeStore
    {
        public List<KnowledgeDocumentMatch> Documents { get; } = [];

        public List<RetrievedChunk> Ranked { get; } = [];

        public List<RetrievedChunk> Decisive { get; } = [];

        public List<KnowledgeFilter> DocumentFilters { get; } = [];

        public List<(KnowledgeFilter Filter, int TopK)> Searches { get; } = [];

        public KnowledgeDocumentMatch AddDocument(string knowledgeNamespace, Guid policyVersionId)
        {
            var document = new KnowledgeDocumentMatch(Guid.NewGuid(), knowledgeNamespace, "Aurora Limited Warranty", 1, policyVersionId);
            Documents.Add(document);
            return document;
        }

        public Task<StoredKnowledgeDocument?> FindDocumentAsync(string knowledgeNamespace, string sourceRef, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Guid> SaveDocumentAsync(KnowledgeDocumentDraft document, IReadOnlyList<KnowledgeChunkDraft> chunks, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<KnowledgeDocumentMatch>> FindDocumentsAsync(KnowledgeFilter filter, CancellationToken ct)
        {
            DocumentFilters.Add(filter);
            return Task.FromResult<IReadOnlyList<KnowledgeDocumentMatch>>(Documents);
        }

        public Task<IReadOnlyList<RetrievedChunk>> SearchChunksAsync(
            KnowledgeFilter filter, ReadOnlyMemory<float> embedding, int topK, CancellationToken ct)
        {
            Searches.Add((filter, topK));
            return Task.FromResult<IReadOnlyList<RetrievedChunk>>(filter.ClauseTypes is null ? Ranked : Decisive);
        }
    }

    private sealed class EmailRedactor : IPiiRedactor
    {
        public RedactionResult Redact(string text)
        {
            var redacted = text.Replace("jane@example.com", "[EMAIL]", StringComparison.Ordinal);
            return new RedactionResult(redacted, redacted == text ? 0 : 1);
        }
    }

    private sealed class TestTenant : ITenantContext
    {
        public bool IsResolved { get; init; } = true;

        public Guid TenantId => IsResolved ? Aurora : throw new InvalidOperationException("No tenant.");

        public string TenantSlug => "aurora";

        public string KnowledgeNamespace => "tenant-aurora";

        public string PrincipalId { get; init; } = "user-1";

        public string PrincipalName => PrincipalId;

        public IReadOnlySet<string> Roles { get; init; } = new HashSet<string>();

        public string CorrelationId => "corr-1";

        public bool IsSystem { get; init; }
    }
}
