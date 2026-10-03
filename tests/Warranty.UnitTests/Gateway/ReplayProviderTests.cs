using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Warranty.AI.Gateway.Prompts;
using Warranty.AI.Gateway.Providers;
using Warranty.AI.Gateway.Providers.Replay;
using Warranty.AI.Gateway.Routing;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Claims;
using Warranty.UnitTests.Infrastructure;

namespace Warranty.UnitTests.Gateway;

public sealed class ReplayProviderTests : IDisposable
{
    private static readonly Guid Aurora = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "warranty-replay-" + Guid.NewGuid().ToString("N"));
    private readonly ReplayCallCounter _counter = new();
    private readonly FixedSelector _selector = new("S1");
    private readonly AiGatewayOptions _options;

    public ReplayProviderTests()
    {
        Directory.CreateDirectory(_root);
        _options = new AiGatewayOptions
        {
            Mode = AiGatewayOptions.ReplayMode,
            Routes = { ["policy-reasoning"] = new RouteOptions { Provider = "anthropic", Model = "claude-opus-5-5" } },
            Replay = { RecordingsPath = _root },
        };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Each_agent_call_on_a_claim_reads_the_next_numbered_fixture()
    {
        Fixture("S1", "policy-1", """
            {"stop":"ToolCalls","toolCalls":[{"callId":"t1","toolName":"warranty_lookup","arguments":{"serial":"AUR-TAB10-0001"}}],
             "usage":{"model":"claude-opus-5-5","inputTokens":900,"outputTokens":40}}
            """);
        Fixture("S1", "policy-2", """{"stop":"Completed","structuredOutput":{"covered":true},"usage":{"inputTokens":1200,"outputTokens":300}}""");
        var claimId = Guid.NewGuid();
        var provider = Replay();

        var first = await provider.CompleteAsync(Turn(claimId), TestContext.Current.CancellationToken);
        var second = await provider.CompleteAsync(Turn(claimId), TestContext.Current.CancellationToken);

        first.Stop.ShouldBe(AiStopKind.ToolCalls);
        var call = first.ToolCalls.ShouldHaveSingleItem();
        call.ToolName.ShouldBe("warranty_lookup");
        call.Arguments.GetProperty("serial").GetString().ShouldBe("AUR-TAB10-0001");
        first.AssistantMessage.Parts.ShouldHaveSingleItem().ShouldBeOfType<ToolCallPart>().CallId.ShouldBe("t1");
        first.Usage.ShouldBe(new AiUsage("replay", "claude-opus-5-5", 900, 40, 0, 0, TimeSpan.Zero, 0m));

        second.Stop.ShouldBe(AiStopKind.Completed);
        second.StructuredOutput!.Value.GetProperty("covered").GetBoolean().ShouldBeTrue();
        second.AssistantMessage.Parts.ShouldHaveSingleItem().ShouldBe(new TextPart("""{"covered":true}"""));
    }

    [Fact]
    public async Task Another_claim_starts_its_own_numbering()
    {
        Fixture("S1", "policy-1", """{"stop":"Completed","structuredOutput":{"covered":false}}""");
        var provider = Replay();

        await provider.CompleteAsync(Turn(Guid.NewGuid()), TestContext.Current.CancellationToken);
        var other = await provider.CompleteAsync(Turn(Guid.NewGuid()), TestContext.Current.CancellationToken);

        other.Stop.ShouldBe(AiStopKind.Completed);
    }

    [Fact]
    public async Task Recorded_failures_replay_as_failures()
    {
        Fixture("S1", "policy-1", """{"stop":"Failed","failure":{"kind":"Transient","message":"overloaded"}}""");

        var result = await Replay().CompleteAsync(Turn(Guid.NewGuid()), TestContext.Current.CancellationToken);

        result.Stop.ShouldBe(AiStopKind.Failed);
        result.Failure.ShouldBe(new AiFailure(AiFailureKind.Transient, "overloaded"));
    }

    [Fact]
    public async Task A_missing_fixture_or_scenario_is_a_failed_turn()
    {
        var missingFixture = await Replay().CompleteAsync(Turn(Guid.NewGuid()), TestContext.Current.CancellationToken);
        _selector.Scenario = null;
        var noScenario = await Replay().CompleteAsync(Turn(Guid.NewGuid()), TestContext.Current.CancellationToken);

        missingFixture.Failure!.Kind.ShouldBe(AiFailureKind.ProviderError);
        missingFixture.Failure.Message.ShouldContain("S1/policy-1.json");
        noScenario.Failure!.Message.ShouldContain("No replay scenario");
    }

    [Fact]
    public async Task Record_mode_calls_the_live_provider_and_writes_a_fixture_that_replays_the_same_turn()
    {
        _options.Replay.Record = true;
        var live = new ScriptedModelProvider("anthropic")
            .Enqueue(ScriptedModelProvider.ToolCalls(new AiToolCall("t9", "warranty_lookup", Json("""{"serial":"X1"}"""))));
        var claimId = Guid.NewGuid();

        var recorded = await Replay(live).CompleteAsync(Turn(claimId), TestContext.Current.CancellationToken);

        recorded.Stop.ShouldBe(AiStopKind.ToolCalls);
        live.Requests.ShouldHaveSingleItem();
        File.Exists(Path.Combine(_root, "S1", "policy-1.json")).ShouldBeTrue();

        _options.Replay.Record = false;
        var replayed = await new ReplayModelProvider(_selector, new ReplayCallCounter(), Options.Create(_options), NullLogger<ReplayModelProvider>.Instance)
            .CompleteAsync(Turn(claimId), TestContext.Current.CancellationToken);
        replayed.Stop.ShouldBe(AiStopKind.ToolCalls);
        replayed.ToolCalls.ShouldHaveSingleItem().CallId.ShouldBe("t9");
        replayed.ToolCalls[0].Arguments.GetProperty("serial").GetString().ShouldBe("X1");
    }

    [Fact]
    public async Task The_scenario_is_found_by_the_claims_serial_number_within_the_tenant()
    {
        var scenarios = Path.Combine(_root, "scenarios.json");
        await File.WriteAllTextAsync(scenarios, """
            [{"scenarioId":"S12-borealis","tenant":"borealis","serial":"shared-001","expected":"Approved"},
             {"scenarioId":"S12-aurora","tenant":"aurora","serial":"shared-001"},
             {"scenarioId":"S1","serial":"aur-tab10-0001"}]
            """, TestContext.Current.CancellationToken);
        _options.Replay.ScenariosPath = scenarios;
        var catalog = ReplayScenarioCatalog.Load(Options.Create(_options));
        var claims = Substitute.For<IClaimRepository>();
        var claim = Claim("SHARED-001");
        claims.GetAsync(claim.Id, Arg.Any<CancellationToken>()).Returns(claim);
        var selector = new SerialNumberScenarioSelector(claims, new FakeTenantContext(Aurora), catalog);

        var scenario = await selector.SelectScenarioAsync(Context(claim.Id), TestContext.Current.CancellationToken);

        scenario.ShouldBe("S12-aurora");
        catalog.Find(" AUR-TAB10-0001 ", "borealis").ShouldBe("S1");
        catalog.Find("unknown", "aurora").ShouldBeNull();
        (await selector.SelectScenarioAsync(Context(Guid.NewGuid()), TestContext.Current.CancellationToken)).ShouldBeNull();
        (await selector.SelectScenarioAsync(Context(null), TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task The_scripted_provider_answers_in_order_and_throws_when_it_runs_out()
    {
        var scripted = new ScriptedModelProvider()
            .Enqueue(ScriptedModelProvider.Completed("""{"ok":1}"""))
            .Enqueue(request => ScriptedModelProvider.Failed(AiFailureKind.Timeout, request.Request.Context.Agent));
        var ct = TestContext.Current.CancellationToken;

        (await scripted.CompleteAsync(Turn(Guid.NewGuid()), ct)).StructuredOutput!.Value.GetProperty("ok").GetInt32().ShouldBe(1);
        (await scripted.CompleteAsync(Turn(Guid.NewGuid()), ct)).Failure.ShouldBe(new AiFailure(AiFailureKind.Timeout, "policy"));
        await Should.ThrowAsync<InvalidOperationException>(() => scripted.CompleteAsync(Turn(Guid.NewGuid()), ct));
        scripted.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Hash_embeddings_are_deterministic_unit_vectors_where_shared_words_are_closer()
    {
        using var generator = new HashEmbeddingGenerator();
        var ct = TestContext.Current.CancellationToken;

        var vectors = await generator.GenerateAsync(
            ["Battery coverage period is 12 months", "battery coverage lasts 12 months", "Liquid damage is excluded", ""], cancellationToken: ct);
        var again = await generator.GenerateAsync(["Battery coverage period is 12 months"], cancellationToken: ct);

        vectors.ShouldAllBe(v => v.Vector.Length == 768);
        foreach (var vector in vectors.Take(3))
        {
            Dot(vector.Vector.Span, vector.Vector.Span).ShouldBe(1f, 1e-5f);
        }

        again[0].Vector.ToArray().ShouldBe(vectors[0].Vector.ToArray());
        Dot(vectors[0].Vector.Span, vectors[1].Vector.Span).ShouldBeGreaterThan(Dot(vectors[0].Vector.Span, vectors[2].Vector.Span));
        vectors[3].Vector.Span[0].ShouldBe(1f);
        vectors.Usage!.InputTokenCount.ShouldBe(15);
        generator.GetService(typeof(Microsoft.Extensions.AI.EmbeddingGeneratorMetadata))
            .ShouldBeOfType<Microsoft.Extensions.AI.EmbeddingGeneratorMetadata>().DefaultModelDimensions.ShouldBe(768);
    }

    private ReplayModelProvider Replay(IModelProvider? live = null)
        => new(_selector, _counter, Options.Create(_options), NullLogger<ReplayModelProvider>.Instance, live);

    private void Fixture(string scenario, string name, string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, scenario));
        File.WriteAllText(Path.Combine(_root, scenario, name + ".json"), json);
    }

    private ResolvedTurnRequest Turn(Guid claimId)
    {
        var route = new ModelProfileRegistry(Options.Create(_options)).Resolve("policy-reasoning");
        var conversation = new AiConversation();
        conversation.AddUser(new TextPart("Assess coverage."));
        var request = new AiTurnRequest(
            Context(claimId), "policy-reasoning", new PromptRef("policy", 1), new Dictionary<string, string>(), conversation, [], null,
            TimeSpan.FromSeconds(30));
        return new ResolvedTurnRequest(
            request, route, new PromptTemplate("policy", 1, "policy-reasoning", null, "Assess."), "Assess.", conversation.Messages, 8000);
    }

    private static AiCallContext Context(Guid? claimId) => new(Aurora, claimId, Guid.NewGuid(), "policy", "corr-1");

    private static Claim Claim(string serial)
        => Warranty.Domain.Claims.Claim.Submit(
            Guid.NewGuid(), Aurora, "ABCDEFGH12", ClaimChannel.ClaimantPortal, "claimant", Guid.NewGuid(), "claimant@example.test", null,
            "AUR-TAB10", null, serial, new DateOnly(2026, 1, 10), "Store", 450m, null, "The screen flickers and then goes black.", DateTimeOffset.UtcNow);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private sealed class FixedSelector(string? scenario) : IReplayScenarioSelector
    {
        public string? Scenario { get; set; } = scenario;

        public Task<string?> SelectScenarioAsync(AiCallContext context, CancellationToken ct) => Task.FromResult(Scenario);
    }
}
