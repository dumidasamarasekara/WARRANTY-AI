using NSubstitute;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Common;
using Warranty.Domain.Policies;
using Warranty.Knowledge.Ingestion;
using Warranty.UnitTests.Infrastructure;

namespace Warranty.UnitTests.Knowledge;

public sealed class KnowledgeIngestionTests
{
    private const string Policy = """
        ---
        namespace: tenant-aurora
        documentType: WarrantyPolicy
        policyCode: aur-wp
        title: Aurora Limited Warranty
        version: 2
        effectiveFrom: 2026-07-01
        effectiveTo: null
        regions: [NA, EU]
        productCategories: []
        classification: Internal
        allowedRoles: [adjudication-service, claims-reviewer, auditor]
        terms:
          standardCoverageMonths: { NA: 12, EU: 24 }
          componentCoverageMonths: { battery: 12 }
          accidentalDamage: { covered: false, windowMonths: 0, maxIncidents: 0 }
          exclusions: [ACCIDENTAL_DAMAGE, LIQUID_DAMAGE]
        clauses:
          AUR-WP-1:   { type: Coverage }
          AUR-WP-2.1: { type: Period }
          AUR-WP-3.2: { type: Exclusion, exclusionCode: ACCIDENTAL_DAMAGE }
        ---
        # Aurora Limited Warranty

        ## AUR-WP-1 Coverage
        Aurora covers defects in materials and workmanship.

        ## AUR-WP-2.1 Coverage period
        Twelve months in North America, twenty-four months in the EU.

        ## AUR-WP-3.2 Exclusions — accidental damage
        Drops, impacts and cracked screens are not covered.
        """;

    private const string Terminology = """
        ---
        namespace: global
        documentType: Terminology
        title: Warranty terminology
        version: 1
        classification: Public
        allowedRoles: [adjudication-service]
        ---
        Shared vocabulary.

        ## Defect
        A failure in materials or workmanship.
        """;

    private static readonly Guid Aurora = Guid.Parse("0199a000-0000-7000-8000-000000000001");

    private readonly FakeStore _store = new();
    private readonly FakePolicies _policies = new();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAiGateway _gateway = Substitute.For<IAiGateway>();

    public KnowledgeIngestionTests()
        => _gateway.EmbedAsync(Arg.Any<AiEmbeddingRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<AiEmbeddingRequest>();
                return new AiEmbeddingResult(
                    request.Inputs.Select(_ => (ReadOnlyMemory<float>)new float[768]).ToList(),
                    "nomic-embed-text",
                    768,
                    AiUsage.None("ollama", "nomic-embed-text"));
            });

    [Fact]
    public void Front_matter_is_parsed_into_typed_values()
    {
        var source = FrontMatterParser.Parse(new KnowledgeSourceDocument("tenant-aurora/policies/AUR-WP-v2.md", Policy));

        source.Namespace.ShouldBe("tenant-aurora");
        source.DocumentType.ShouldBe(DocumentType.WarrantyPolicy);
        source.PolicyCode.ShouldBe("AUR-WP");
        source.EffectiveFrom.ShouldBe(new DateOnly(2026, 7, 1));
        source.EffectiveTo.ShouldBeNull();
        source.Regions.ShouldBe([Region.NA, Region.EU]);
        source.Classification.ShouldBe(DocumentClassification.Internal);
        source.Terms!.StandardCoverageMonths[Region.EU].ShouldBe(24);
        source.Terms.ComponentCoverageMonths["battery"].ShouldBe(12);
        source.Terms.Exclusions.ShouldBe([ExclusionCode.AccidentalDamage, ExclusionCode.LiquidDamage]);
        source.Clauses["AUR-WP-3.2"].ShouldBe(new ClauseDeclaration(ClauseType.Exclusion, ExclusionCode.AccidentalDamage));
        source.Body.ShouldStartWith("# Aurora Limited Warranty");
    }

    [Theory]
    [InlineData("no front matter", "'---' front-matter block")]
    [InlineData("---\ntitle: x\n", "not closed")]
    [InlineData("---\ntitle: [unclosed\n---\n", "not valid YAML")]
    [InlineData("---\ntitel: typo\n---\n", "not valid YAML")]
    [InlineData("---\nnamespace: global\ndocumentType: Brochure\ntitle: x\nversion: 1\n---\n", "documentType 'Brochure'")]
    [InlineData("---\nnamespace: global\ndocumentType: Terminology\ntitle: x\n---\n", "version is required")]
    [InlineData("---\nnamespace: global\ndocumentType: Terminology\ntitle: x\nversion: 1\neffectiveFrom: 01.07.2026\n---\n", "yyyy-MM-dd")]
    public void Malformed_front_matter_is_rejected(string content, string expected)
        => Should.Throw<InvalidKnowledgeSourceException>(() => FrontMatterParser.Parse(new KnowledgeSourceDocument("x.md", content)))
            .Message.ShouldContain(expected);

    [Fact]
    public void Policy_bodies_split_at_clause_headings_and_long_clauses_repeat_their_key()
    {
        var body = "# Title\n\n## AUR-WP-1 Coverage\nFirst paragraph.\n\nSecond paragraph.\n\n## AUR-WP-2\nShort.\n### Not a clause\nStill AUR-WP-2.";

        var sections = ClauseChunker.Sections(body, keyedHeadings: true);
        var chunks = ClauseChunker.Chunk(sections, maxChars: 20);

        sections.Select(s => (s.ClauseKey, s.Title)).ShouldBe([("AUR-WP-1", "Coverage"), ("AUR-WP-2", "AUR-WP-2")]);
        sections[1].Text.ShouldBe("Short.\n### Not a clause\nStill AUR-WP-2.");
        chunks.Select(c => (c.Index, c.ClauseKey, c.Text)).ShouldBe(
        [
            (0, "AUR-WP-1", "First paragraph."),
            (1, "AUR-WP-1", "Second paragraph."),
            (2, "AUR-WP-2", "Short.\n### Not a clause\nStill AUR-WP-2."),
        ]);
        chunks[1].EmbeddingInput.ShouldBe("AUR-WP-1 Coverage\n\nSecond paragraph.");
    }

    [Fact]
    public void Other_documents_keep_their_preamble_and_titled_sections()
    {
        var sections = ClauseChunker.Sections("# Terms\nIntro.\n\n## Defect\nA failure.\n\n## Empty\n", keyedHeadings: false);

        sections.ShouldBe([new KnowledgeSection(null, null, "Intro."), new KnowledgeSection(null, "Defect", "A failure.")]);
    }

    [Theory]
    [InlineData("namespace: tenant-aurora", "namespace: tenant-borealis", "neither 'global' nor 'tenant-aurora'")]
    [InlineData("AUR-WP-2.1: { type: Period }", "AUR-WP-2.1: { type: Period }\n  AUR-WP-9: { type: Coverage }", "clauses.AUR-WP-9 has no '## AUR-WP-9")]
    [InlineData("  AUR-WP-1:   { type: Coverage }\n", "", "clause AUR-WP-1 has no type")]
    [InlineData("exclusionCode: ACCIDENTAL_DAMAGE }", "exclusionCode: COSMETIC_DAMAGE }", "names COSMETIC_DAMAGE, which terms.exclusions does not list")]
    [InlineData("AUR-WP-3.2: { type: Exclusion, exclusionCode: ACCIDENTAL_DAMAGE }", "AUR-WP-3.2: { type: Exclusion }", "must name an exclusionCode")]
    [InlineData("AUR-WP-1:   { type: Coverage }", "AUR-WP-1:   { type: Coverage, exclusionCode: LIQUID_DAMAGE }", "only Exclusion clauses")]
    [InlineData("# Aurora Limited Warranty", "Stray text.", "belongs to no clause")]
    [InlineData("allowedRoles: [adjudication-service, claims-reviewer, auditor]", "allowedRoles: []", "allowedRoles must name")]
    [InlineData("productCategories: []", "productCategories: [tablet, phone]", "at most one category")]
    [InlineData("standardCoverageMonths: { NA: 12, EU: 24 }", "standardCoverageMonths: { NA: 12 }", "region EU")]
    public void Invalid_policy_sources_are_rejected(string find, string replace, string expected)
    {
        var source = FrontMatterParser.Parse(new KnowledgeSourceDocument("p.md", Policy.Replace(find, replace, StringComparison.Ordinal)));
        var sections = ClauseChunker.Sections(source.Body, keyedHeadings: true);

        Should.Throw<InvalidKnowledgeSourceException>(() => Validator(Aurora).Validate(source, sections, []))
            .Errors.ShouldContain(e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Global_knowledge_is_ingested_only_without_a_tenant_and_tenant_knowledge_only_with_its_tenant()
    {
        var global = FrontMatterParser.Parse(new KnowledgeSourceDocument("g.md", Terminology));
        var policy = FrontMatterParser.Parse(new KnowledgeSourceDocument("p.md", Policy));

        Should.NotThrow(() => Validator(null).Validate(global, ClauseChunker.Sections(global.Body, false), []));
        Should.Throw<InvalidKnowledgeSourceException>(() => Validator(Aurora).Validate(global, ClauseChunker.Sections(global.Body, false), []))
            .Errors.ShouldContain(e => e.Contains("ingested by the platform", StringComparison.Ordinal));
        Should.Throw<InvalidKnowledgeSourceException>(() => Validator(null).Validate(policy, ClauseChunker.Sections(policy.Body, true), []))
            .Errors.ShouldContain(e => e.Contains("needs the tenant being ingested", StringComparison.Ordinal));
    }

    [Fact]
    public void Versions_of_one_policy_must_not_overlap()
    {
        var source = FrontMatterParser.Parse(new KnowledgeSourceDocument("p.md", Policy));
        var policy = WarrantyPolicy.Create(Guid.NewGuid(), Aurora, "AUR-WP", "Aurora");
        var v1 = Version(policy, 1, new DateOnly(2025, 1, 1), new DateOnly(2026, 7, 31));

        Should.Throw<InvalidKnowledgeSourceException>(() => Validator(Aurora).Validate(source, ClauseChunker.Sections(source.Body, true), [v1]))
            .Errors.ShouldContain(e => e.Contains("overlaps version 1", StringComparison.Ordinal));
        Should.NotThrow(() => Validator(Aurora).Validate(
            source, ClauseChunker.Sections(source.Body, true), [Version(policy, 1, new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 30))]));
    }

    [Fact]
    public async Task A_new_policy_version_creates_policy_rows_and_indexes_typed_clause_chunks()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await Ingestor(Aurora).IngestAsync(new KnowledgeSourceDocument("tenant-aurora/policies/AUR-WP-v2.md", Policy), ct);

        result.Skipped.ShouldBeFalse();
        result.Namespace.ShouldBe("tenant-aurora");
        result.ChunkCount.ShouldBe(3);

        var policy = _policies.Policies.ShouldHaveSingleItem();
        policy.Code.ShouldBe("AUR-WP");
        var version = _policies.Versions.ShouldHaveSingleItem();
        version.Version.ShouldBe(2);
        version.TenantId.ShouldBe(Aurora);
        version.SourceBlobPath.ShouldBe("tenant-aurora/policies/AUR-WP-v2.md");
        version.Terms.Excludes(ExclusionCode.LiquidDamage).ShouldBeTrue();
        _policies.Clauses.Select(c => (c.ClauseKey, c.ClauseType, c.ExclusionCode)).ShouldBe(
        [
            ("AUR-WP-1", ClauseType.Coverage, (ExclusionCode?)null),
            ("AUR-WP-2.1", ClauseType.Period, null),
            ("AUR-WP-3.2", ClauseType.Exclusion, ExclusionCode.AccidentalDamage),
        ]);

        var (document, chunks) = _store.Saved.ShouldHaveSingleItem();
        document.SourceRef.ShouldBe($"policy_version:{version.Id}");
        document.Classification.ShouldBe(DocumentClassification.Internal);
        document.ProductCategory.ShouldBeNull();
        document.EmbeddingModel.ShouldBe("nomic-embed-text");
        chunks.Select(c => (c.ClauseKey, c.ClauseType, c.ExclusionCode, c.PolicyVersionId)).ShouldBe(
        [
            ("AUR-WP-1", ClauseType.Coverage, (ExclusionCode?)null, (Guid?)version.Id),
            ("AUR-WP-2.1", ClauseType.Period, null, version.Id),
            ("AUR-WP-3.2", ClauseType.Exclusion, ExclusionCode.AccidentalDamage, version.Id),
        ]);
        chunks[2].Text.ShouldBe("Drops, impacts and cracked screens are not covered.");

        await _gateway.Received(1).EmbedAsync(
            Arg.Is<AiEmbeddingRequest>(r => r.Context.TenantId == Aurora && r.Context.Agent == KnowledgeIngestor.AgentName
                                            && r.Route == "embedding" && r.Inputs.Count == 3),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unchanged_source_is_skipped_and_changed_content_for_a_published_version_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var ingestor = Ingestor(Aurora);
        var first = await ingestor.IngestAsync(new KnowledgeSourceDocument("p.md", Policy), ct);

        var again = await ingestor.IngestAsync(new KnowledgeSourceDocument("p.md", Policy), ct);

        again.ShouldBe(first with { Skipped = true });
        _store.Saved.Count.ShouldBe(1);
        _policies.Versions.Count.ShouldBe(1);
        await _gateway.Received(1).EmbedAsync(Arg.Any<AiEmbeddingRequest>(), Arg.Any<CancellationToken>());

        var edited = Policy.Replace("Drops, impacts", "Drops and impacts", StringComparison.Ordinal);
        Should.Throw<InvalidKnowledgeSourceException>(() => ingestor.IngestAsync(new KnowledgeSourceDocument("p.md", edited), ct))
            .Message.ShouldContain("publish it as a new version");
    }

    [Fact]
    public async Task Global_knowledge_is_embedded_as_platform_work()
    {
        var result = await Ingestor(null).IngestAsync(new KnowledgeSourceDocument(@"global\terminology.md", Terminology), TestContext.Current.CancellationToken);

        result.Namespace.ShouldBe("global");
        var (document, chunks) = _store.Saved.ShouldHaveSingleItem();
        document.SourceRef.ShouldBe("source:global/terminology.md");
        chunks.Select(c => (c.ClauseKey, c.SectionTitle, c.Text, c.ClauseType)).ShouldBe(
            [(null, null, "Shared vocabulary.", null), (null, "Defect", "A failure in materials or workmanship.", (ClauseType?)null)]);
        _policies.Versions.ShouldBeEmpty();
        await _gateway.Received(1).EmbedAsync(Arg.Is<AiEmbeddingRequest>(r => r.Context.TenantId == Guid.Empty), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private KnowledgeIngestor Ingestor(Guid? tenant)
    {
        var context = new FakeTenantContext(tenant);
        return new KnowledgeIngestor(_store, _policies, _unitOfWork, _gateway, context, new KnowledgeSourceValidator(context));
    }

    private static KnowledgeSourceValidator Validator(Guid? tenant) => new(new FakeTenantContext(tenant));

    private static PolicyVersion Version(WarrantyPolicy policy, int number, DateOnly from, DateOnly? to)
        => PolicyVersion.Create(
            Guid.NewGuid(), policy.TenantId, policy.Id, number, from, to, [Region.NA, Region.EU], [],
            new CoverageTerms(new Dictionary<Region, int> { [Region.NA] = 12, [Region.EU] = 24 }, new Dictionary<string, int>(), AccidentalDamageTerms.NotCovered, []),
            "p.md", "abc");

    private sealed class FakeStore : IKnowledgeStore
    {
        public List<(KnowledgeDocumentDraft Document, IReadOnlyList<KnowledgeChunkDraft> Chunks)> Saved { get; } = [];

        private readonly Dictionary<(string, string), StoredKnowledgeDocument> _documents = [];

        public Task<StoredKnowledgeDocument?> FindDocumentAsync(string knowledgeNamespace, string sourceRef, CancellationToken ct)
            => Task.FromResult(_documents.GetValueOrDefault((knowledgeNamespace, sourceRef)));

        public Task<Guid> SaveDocumentAsync(KnowledgeDocumentDraft document, IReadOnlyList<KnowledgeChunkDraft> chunks, CancellationToken ct)
        {
            Saved.Add((document, chunks));
            var id = Guid.NewGuid();
            _documents[(document.Namespace, document.SourceRef)] = new StoredKnowledgeDocument(
                id, document.Namespace, document.SourceRef, document.Checksum, document.EmbeddingModel, document.EmbeddingDim, chunks.Count);
            return Task.FromResult(id);
        }

        public Task<IReadOnlyList<KnowledgeDocumentMatch>> FindDocumentsAsync(KnowledgeFilter filter, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<RetrievedChunk>> SearchChunksAsync(
            KnowledgeFilter filter, ReadOnlyMemory<float> embedding, int topK, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakePolicies : IPolicyRepository
    {
        public List<WarrantyPolicy> Policies { get; } = [];

        public List<PolicyVersion> Versions { get; } = [];

        public List<PolicyClause> Clauses { get; } = [];

        public Task<IReadOnlyList<PolicyVersion>> GetVersionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PolicyVersion>>(Versions);

        public Task<PolicyVersion?> GetVersionAsync(Guid policyVersionId, CancellationToken ct)
            => Task.FromResult(Versions.SingleOrDefault(v => v.Id == policyVersionId));

        public Task<IReadOnlyList<PolicyClause>> GetClausesAsync(Guid policyVersionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<PolicyClause>>(Clauses.Where(c => c.PolicyVersionId == policyVersionId).ToList());

        public Task<WarrantyPolicy?> FindPolicyByCodeAsync(string code, CancellationToken ct)
            => Task.FromResult(Policies.SingleOrDefault(p => p.Code == code));

        public Task<WarrantyPolicy?> GetPolicyAsync(Guid policyId, CancellationToken ct)
            => Task.FromResult(Policies.SingleOrDefault(p => p.Id == policyId));

        public void AddPolicy(WarrantyPolicy policy) => Policies.Add(policy);

        public void AddVersion(PolicyVersion version) => Versions.Add(version);

        public void AddClause(PolicyClause clause) => Clauses.Add(clause);
    }
}
