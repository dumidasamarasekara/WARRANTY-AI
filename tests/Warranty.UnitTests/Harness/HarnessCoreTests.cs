using System.Text.Json;
using NSubstitute;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Execution;
using Warranty.AI.Harness.Tools;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Domain.Claims;
using Warranty.Domain.Common;

namespace Warranty.UnitTests.Harness;

public sealed class HarnessCoreTests
{
    private static readonly Guid Aurora = Guid.Parse("0199a000-0000-7000-8000-000000000001");
    private static readonly Guid RunId = Guid.Parse("0199a000-0000-7000-8000-0000000000aa");
    private static readonly Guid ClaimId = Guid.Parse("0199a000-0000-7000-8000-0000000000cc");

    private static readonly AgentDescriptor Policy = new(
        "policy", "policy-reasoning", new PromptRef("policy", 1), "policy-assessment", ["warranty_lookup"], MaxTurns: 3, InputTokenBudget: 16_000);

    private readonly IAiGateway _gateway = Substitute.For<IAiGateway>();
    private readonly RecordingTools _tools = new();

    [Fact]
    public void References_are_numbered_per_kind_and_reissued_for_the_same_target()
    {
        var registry = new ReferenceRegistry();
        var evidence = Guid.NewGuid();

        registry.IssueEvidence(evidence).ShouldBe("EV-1");
        registry.IssueEvidence(Guid.NewGuid()).ShouldBe("EV-2");
        registry.IssueEvidence(evidence).ShouldBe("EV-1");
        registry.IssueChunk(Chunk("tenant-aurora")).ShouldBe("POL-1");
        registry.IssueChunk(Chunk("global")).ShouldBe("GLB-1");

        registry.Resolve("EV-1", ReferenceKind.Evidence).TargetId.ShouldBe(evidence);
        registry.Resolve("POL-1", ReferenceKind.Policy).Chunk!.Namespace.ShouldBe("tenant-aurora");
    }

    [Fact]
    public void Unknown_or_wrongly_typed_references_are_rejected()
    {
        var registry = new ReferenceRegistry();
        registry.IssueChunk(Chunk("global"));
        registry.IssueEvidence(Guid.NewGuid());

        registry.Validate(["GLB-1", "POL-7", "EV-1", "chunk:123"], ReferenceKind.Policy).ShouldBe(
        [
            "GLB-1 is not a POL-n reference.",
            "POL-7 was not issued for this run.",
            "EV-1 is not a POL-n reference.",
            "'chunk:123' is not a reference ID.",
        ]);
        Should.Throw<UnknownReferenceException>(() => registry.Resolve("EV-2", ReferenceKind.Evidence)).Reference.ShouldBe("EV-2");
        registry.TryResolve("POL-1", out _).ShouldBeFalse();
    }

    [Fact]
    public void The_reference_map_round_trips_and_numbering_continues()
    {
        var registry = new ReferenceRegistry();
        registry.IssueEvidence(Guid.NewGuid());
        var policy = Chunk("tenant-aurora");
        registry.IssueChunk(policy);
        registry.IssueChunk(Chunk("tenant-aurora"));

        var restored = ReferenceRegistry.FromMap(registry.ToMap());

        restored.ToMap().ShouldBe(registry.ToMap());
        restored.Resolve("POL-1", ReferenceKind.Policy).TargetId.ShouldBe(policy.ChunkId);
        restored.IssueChunk(policy).ShouldBe("POL-1");
        restored.IssueChunk(Chunk("tenant-aurora")).ShouldBe("POL-3");
        restored.IssueEvidence(Guid.NewGuid()).ShouldBe("EV-2");
    }

    [Fact]
    public void Context_is_ordered_by_priority_and_other_clauses_by_score()
    {
        var result = new ContextBuilder().Build(
        [
            ContextItem.Text("glb", ContextPriority.GlobalSnippets, "g"),
            ContextItem.Text("pol-low", ContextPriority.OtherClauses, "l", score: 0.2),
            ContextItem.Text("facts", ContextPriority.CaseFacts, "f"),
            ContextItem.Text("pol-high", ContextPriority.OtherClauses, "h", score: 0.9),
            ContextItem.Text("period", ContextPriority.DecisiveClauses, "p"),
            ContextItem.Text("instructions", ContextPriority.Instructions, "i"),
        ],
            tokenBudget: 1_000);

        result.Status.ShouldBe(ContextBuildStatus.Ok);
        result.Included.ShouldBe(["instructions", "facts", "period", "pol-high", "pol-low", "glb"]);
        result.Dropped.ShouldBeEmpty();
        result.Parts.OfType<TextPart>().Select(p => p.Text).ShouldBe(["i", "f", "p", "h", "l", "g"]);
    }

    [Fact]
    public void Over_budget_drops_global_snippets_then_the_lowest_scored_clauses()
    {
        var text = new string('x', 400); // 100 tokens + overhead each
        var result = new ContextBuilder().Build(
        [
            ContextItem.Text("instructions", ContextPriority.Instructions, text),
            ContextItem.Text("period", ContextPriority.DecisiveClauses, text),
            ContextItem.Text("pol-high", ContextPriority.OtherClauses, text, score: 0.9),
            ContextItem.Text("pol-low", ContextPriority.OtherClauses, text, score: 0.1),
            ContextItem.Text("glb", ContextPriority.GlobalSnippets, text),
        ],
            tokenBudget: 3 * 108);

        result.Status.ShouldBe(ContextBuildStatus.Ok);
        result.Dropped.ShouldBe(["glb", "pol-low"]);
        result.Included.ShouldBe(["instructions", "period", "pol-high"]);
        result.EstimatedTokens.ShouldBe(3 * 108);
    }

    [Fact]
    public void Evidence_is_never_dropped_and_overflow_is_reported_instead()
    {
        var evidence = new ContextItem("EV-1", ContextPriority.CaseFacts, [new UntrustedTextPart("invoice", new string('x', 4_000))], IsEvidence: true);

        var result = new ContextBuilder().Build([ContextItem.Text("instructions", ContextPriority.Instructions, "i"), evidence], tokenBudget: 500);

        result.Status.ShouldBe(ContextBuildStatus.ContextOverflow);
        result.Parts.ShouldBeEmpty();
        result.EstimatedTokens.ShouldBeGreaterThan(500);
    }

    [Fact]
    public void Attachments_are_outside_the_text_budget()
        => ContextBuilder.Estimate(new ImagePart("EV-2", new byte[5_000_000], "image/jpeg")).ShouldBe(0);

    [Fact]
    public async Task The_loop_runs_every_tool_call_of_a_turn_and_returns_all_results_in_one_message()
    {
        _gateway.CompleteAsync(Arg.Any<AiTurnRequest>(), Arg.Any<CancellationToken>()).Returns(
            ToolTurn(("c1", "warranty_lookup"), ("c2", "warranty_lookup")),
            FinalTurn("""{"applies":true}"""));
        var conversation = Conversation();

        var result = await new AgentTurnLoop().RunAsync(Request(conversation), Execution(), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(AgentLoopOutcome.Completed);
        result.Status.ShouldBe(AgentStatus.Succeeded);
        result.Turns.ShouldBe(2);
        result.LastTurn.StructuredOutput!.Value.GetProperty("applies").GetBoolean().ShouldBeTrue();
        _tools.Calls.ShouldBe(["c1", "c2"]);

        conversation.Messages.Select(m => m.Role).ShouldBe([AiRole.User, AiRole.Assistant, AiRole.User, AiRole.Assistant]);
        conversation.Messages[2].Parts.OfType<ToolResultPart>().Select(p => p.CallId).ShouldBe(["c1", "c2"]);

        var requests = _gateway.ReceivedCalls().Select(c => (AiTurnRequest)c.GetArguments()[0]!).ToList();
        requests.ShouldAllBe(r => r.Route == "policy-reasoning" && r.Context == new AiCallContext(Aurora, ClaimId, RunId, "policy", "corr-1"));
        requests[0].Tools.Select(t => t.Name).ShouldBe(["warranty_lookup"]);
    }

    [Fact]
    public async Task The_loop_stops_at_max_turns()
    {
        _gateway.CompleteAsync(Arg.Any<AiTurnRequest>(), Arg.Any<CancellationToken>()).Returns(_ => ToolTurn(("c", "warranty_lookup")));

        var result = await new AgentTurnLoop().RunAsync(Request(Conversation()), Execution(), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(AgentLoopOutcome.MaxTurnsReached);
        result.Status.ShouldBe(AgentStatus.Failed);
        result.Turns.ShouldBe(Policy.MaxTurns);
        await _gateway.Received(Policy.MaxTurns).CompleteAsync(Arg.Any<AiTurnRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(AiFailureKind.Timeout, AgentStatus.TimedOut)]
    [InlineData(AiFailureKind.InvalidOutput, AgentStatus.InvalidOutput)]
    [InlineData(AiFailureKind.ProviderError, AgentStatus.Failed)]
    public async Task A_failed_turn_ends_the_loop_without_appending_to_the_conversation(AiFailureKind kind, AgentStatus status)
    {
        _gateway.CompleteAsync(Arg.Any<AiTurnRequest>(), Arg.Any<CancellationToken>()).Returns(new AiTurnResult(
            AiStopKind.Failed, new AiMessage(AiRole.Assistant, []), [], null, AiUsage.None("p", "m"), new AiFailure(kind, "boom")));
        var conversation = Conversation();

        var result = await new AgentTurnLoop().RunAsync(Request(conversation), Execution(), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(AgentLoopOutcome.Failed);
        result.Status.ShouldBe(status);
        conversation.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public void A_run_context_requires_a_resolved_tenant()
        => Should.Throw<InvalidOperationException>(() => new AdjudicationContext(RunId, new Tenant(resolved: false), Case(), new ReferenceRegistry()));

    private static RetrievedChunk Chunk(string knowledgeNamespace)
        => new(Guid.NewGuid(), knowledgeNamespace, Guid.NewGuid(), "Doc", 1, "K-1", "Title", "text", null, null, 0.5);

    private static AiConversation Conversation()
    {
        var conversation = new AiConversation();
        conversation.AddUser(new TextPart("Assess the claim."));
        return conversation;
    }

    private static AgentTurnLoopRequest Request(AiConversation conversation)
        => new(Policy, conversation, new Dictionary<string, string>(), null, AgentTurnLoopRequest.DefaultTurnTimeout);

    private AgentExecutionContext Execution()
        => new(new AdjudicationContext(RunId, new Tenant(), Case(), new ReferenceRegistry()), _gateway, _tools, new ActivityTraceWriter());

    private static AiTurnResult ToolTurn(params (string Id, string Tool)[] calls)
    {
        var toolCalls = calls.Select(c => new AiToolCall(c.Id, c.Tool, JsonSerializer.SerializeToElement(new { component = "battery" }))).ToList();
        return new AiTurnResult(
            AiStopKind.ToolCalls,
            new AiMessage(AiRole.Assistant, toolCalls.Select(c => (AiContentPart)new ToolCallPart(c.CallId, c.ToolName, c.Arguments)).ToList()),
            toolCalls,
            null,
            AiUsage.None("p", "m"),
            null);
    }

    private static AiTurnResult FinalTurn(string json)
        => new(AiStopKind.Completed, new AiMessage(AiRole.Assistant, [new TextPart(json)]), [], JsonDocument.Parse(json).RootElement, AiUsage.None("p", "m"), null);

    private static CaseContext Case() => new(
        ClaimId, 1, "ABCDEFGHJK", ClaimChannel.ClaimantPortal, new DateOnly(2026, 9, 1), new DateOnly(2026, 3, 1), "Store", 450m, "EUR",
        Region.EU, "AUR-TAB10", "SN-1", "Screen flickers.", null, new CaseCustomerView("NO", Region.EU), [], ClaimHistoryCounts.None, false, 0);

    private sealed class RecordingTools : IToolInvoker
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<AiToolDefinition> Definitions { get; } =
        [
            new("warranty_lookup", "Lookup", JsonSerializer.SerializeToElement(new { type = "object" })),
            new("notify_customer", "Not for this agent", JsonSerializer.SerializeToElement(new { type = "object" })),
        ];

        public Task<AiToolResult> InvokeAsync(AiToolCall call, CancellationToken ct)
        {
            Calls.Add(call.CallId);
            return Task.FromResult(new AiToolResult(call.CallId, JsonSerializer.SerializeToElement(new { ok = true }), false));
        }
    }

    private sealed class Tenant(bool resolved = true) : ITenantContext
    {
        public bool IsResolved => resolved;

        public Guid TenantId => Aurora;

        public string TenantSlug => "aurora";

        public string KnowledgeNamespace => "tenant-aurora";

        public string PrincipalId => Principals.AdjudicationService;

        public string PrincipalName => Principals.AdjudicationService;

        public IReadOnlySet<string> Roles { get; } = new HashSet<string> { Principals.AdjudicationService };

        public string CorrelationId => "corr-1";

        public bool IsSystem => true;
    }
}
