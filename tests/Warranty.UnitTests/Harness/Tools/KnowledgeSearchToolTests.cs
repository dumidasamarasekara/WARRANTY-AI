using NSubstitute;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Common;
using Warranty.Domain.Policies;
using static Warranty.UnitTests.Harness.Tools.ToolTestKit;

namespace Warranty.UnitTests.Harness.Tools;

/// <summary><c>search_policy_knowledge</c> (Policy) and <c>search_global_knowledge</c> (Policy, Decision).</summary>
public sealed class KnowledgeSearchToolTests
{
    private readonly IKnowledgeRetriever _retriever = Substitute.For<IKnowledgeRetriever>();
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly ICatalogRepository _catalog = Substitute.For<ICatalogRepository>();
    private readonly ReferenceRegistry _references = new();
    private readonly List<KnowledgeSearchQuery> _queries = [];

    public KnowledgeSearchToolTests()
    {
        _claims.GetAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(NewClaim());
        _catalog.GetProductAsync(ProductId, Arg.Any<CancellationToken>()).Returns(NewProduct());
        _retriever.SearchAsync(Arg.Do<KnowledgeSearchQuery>(q => _queries.AddRange(q is null ? [] : [q])), Arg.Any<CancellationToken>())
            .Returns(new RetrievalResult([Chunk("tenant-aurora", "AUR-WP-3.2", ClauseType.Exclusion, ExclusionCode.AccidentalDamage)], RetrievalOutcome.Ok));
    }

    private SearchPolicyKnowledgeTool PolicyTool => new(_retriever, _claims, _catalog);

    private SearchGlobalKnowledgeTool GlobalTool => new(_retriever);

    [Fact]
    public void The_policy_search_takes_only_a_query_and_is_for_the_policy_agent()
    {
        ShouldBeStrictReadOnlyTool(PolicyTool, "search_policy_knowledge", AgentNames.Policy);
        AllPropertyNames(PolicyTool.Descriptor.InputSchema).ShouldBe(["query"]);
    }

    [Fact]
    public async Task The_policy_search_uses_the_claims_filters_in_the_tenant_namespace_and_issues_POL_references()
    {
        var result = await PolicyTool.InvokeAsync(Args("""{"query":"accidental damage"}"""), Context(AgentNames.Policy, _references), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        var query = _queries.ShouldHaveSingleItem();
        (query.QueryText, query.Scope, query.TopK).ShouldBe(("accidental damage", KnowledgeScope.Tenant, SearchPolicyKnowledgeTool.TopK));
        query.DocumentTypes.ShouldBe([DocumentType.WarrantyPolicy]);
        query.Applicability.ShouldBe(new PolicyApplicability("tablet", ModelCode, Region.EU, PurchaseDate));
        query.Attribution.ShouldBe(new RetrievalAttribution(AgentNames.Policy, RunId, ClaimId));

        var clause = result.Content.GetProperty("clauses").EnumerateArray().ToList().ShouldHaveSingleItem();
        clause.GetProperty("ref").GetString().ShouldBe("POL-1");
        (clause.GetProperty("clauseKey").GetString(), clause.GetProperty("clauseType").GetString(), clause.GetProperty("exclusionCode").GetString())
            .ShouldBe(("AUR-WP-3.2", "Exclusion", "ACCIDENTAL_DAMAGE"));
        _references.Resolve("POL-1", ReferenceKind.Policy).Chunk!.ClauseKey.ShouldBe("AUR-WP-3.2");
    }

    [Fact]
    public async Task The_policy_search_never_returns_chunks_outside_the_tenant_namespace()
    {
        _retriever.SearchAsync(default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(new RetrievalResult(
            [Chunk("global", null, null), Chunk("tenant-borealis", "BOR-WP-1", ClauseType.Coverage)], RetrievalOutcome.Ok));

        var result = await PolicyTool.InvokeAsync(Args("""{"query":"coverage"}"""), Context(AgentNames.Policy, _references), TestContext.Current.CancellationToken);

        result.Content.GetProperty("clauses").EnumerateArray().ShouldBeEmpty();
        _references.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_scope_violation_is_an_error()
    {
        _retriever.SearchAsync(default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(RetrievalResult.Empty(RetrievalOutcome.ScopeViolation));

        (await PolicyTool.InvokeAsync(Args("""{"query":"coverage"}"""), Context(AgentNames.Policy, _references), TestContext.Current.CancellationToken))
            .IsError.ShouldBeTrue();
        (await GlobalTool.InvokeAsync(Args("""{"query":"coverage"}"""), Context(AgentNames.Decision, _references), TestContext.Current.CancellationToken))
            .IsError.ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{"query":"  "}""")]
    [InlineData("""{"query":""}""")]
    public async Task A_blank_query_is_an_error(string arguments)
    {
        (await PolicyTool.InvokeAsync(Args(arguments), Context(AgentNames.Policy, _references), TestContext.Current.CancellationToken)).IsError.ShouldBeTrue();
        (await GlobalTool.InvokeAsync(Args(arguments), Context(AgentNames.Policy, _references), TestContext.Current.CancellationToken)).IsError.ShouldBeTrue();
        _queries.ShouldBeEmpty();
    }

    [Fact]
    public void The_global_search_takes_a_query_and_an_optional_global_document_type_and_serves_policy_and_decision()
    {
        ShouldBeStrictReadOnlyTool(GlobalTool, "search_global_knowledge", AgentNames.Policy, AgentNames.Decision);
        var schema = GlobalTool.Descriptor.InputSchema;
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldBe(["query"]);
        schema.GetProperty("properties").GetProperty("documentType").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["Terminology", "FraudPattern", "Procedure"]);
    }

    [Fact]
    public async Task The_global_search_reads_only_the_global_namespace_and_issues_GLB_references()
    {
        _retriever.SearchAsync(default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(new RetrievalResult(
            [Chunk("global", null, null), Chunk("tenant-aurora", "AUR-WP-1", ClauseType.Coverage)], RetrievalOutcome.Ok));

        var result = await GlobalTool.InvokeAsync(Args("""{"query":"what is accidental damage"}"""), Context(AgentNames.Decision, _references), TestContext.Current.CancellationToken);

        var query = _queries.ShouldHaveSingleItem();
        (query.Scope, query.Applicability).ShouldBe((KnowledgeScope.Global, (PolicyApplicability?)null));
        query.DocumentTypes.ShouldBe(SearchGlobalKnowledgeTool.GlobalDocumentTypes);
        var snippet = result.Content.GetProperty("snippets").EnumerateArray().ToList().ShouldHaveSingleItem();
        snippet.GetProperty("ref").GetString().ShouldBe("GLB-1");
        result.Content.GetProperty("note").GetString()!.ShouldContain("must not be cited as policy");
        _references.Validate(["GLB-1"], ReferenceKind.Policy).ShouldNotBeEmpty("a GLB-n is never citable as policy");
        _references.Entries.Select(e => e.Id).ShouldBe(["GLB-1"]);
    }

    [Fact]
    public async Task The_global_search_can_be_narrowed_to_one_document_type()
    {
        await GlobalTool.InvokeAsync(Args("""{"query":"fraud","documentType":"FraudPattern"}"""), Context(AgentNames.Policy, _references), TestContext.Current.CancellationToken);

        _queries.ShouldHaveSingleItem().DocumentTypes.ShouldBe([DocumentType.FraudPattern]);
    }

    private static RetrievedChunk Chunk(string @namespace, string? clauseKey, ClauseType? type, ExclusionCode? exclusion = null)
        => new(
            Guid.NewGuid(), @namespace, Guid.NewGuid(), @namespace == "global" ? "Warranty terminology" : "Aurora Limited Warranty", 2, clauseKey,
            "Section", "Clause text.", @namespace == "global" ? null : new DateOnly(2026, 1, 1), null, 0.8,
            @namespace == "global" ? null : Aurora, null, type, exclusion);
}
