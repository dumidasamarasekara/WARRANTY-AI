using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Warranty.AI.Gateway;
using Warranty.AI.Gateway.Prompts;
using Warranty.AI.Gateway.Providers;
using Warranty.AI.Gateway.RateLimiting;
using Warranty.AI.Gateway.Routing;
using Warranty.AI.Gateway.Usage;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.AiOps;
using Warranty.UnitTests.Infrastructure;

namespace Warranty.UnitTests.Gateway;

public sealed class AiGatewayTests : IDisposable
{
    private static readonly Guid Aurora = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","required":["decision"],"additionalProperties":false,
         "properties":{"decision":{"enum":["Approve","Reject"]}}}
        """).RootElement;

    private readonly FakeProvider _anthropic = new("anthropic");
    private readonly FakeProvider _replay = new("replay");
    private readonly FakeEmbeddings _embeddings = new();
    private readonly IAiOpsRepository _aiOps = Substitute.For<IAiOpsRepository>();
    private readonly AiGatewayOptions _options = new()
    {
        Routes =
        {
            ["adjudication"] = new RouteOptions { Provider = "anthropic", Model = "claude-opus-5-5", MaxTokens = 16_000, TimeoutSeconds = 5 },
            ["extraction"] = new RouteOptions { Provider = "anthropic", Model = "claude-haiku-4-5", MaxTokens = 100_000 },
            ["embedding"] = new RouteOptions { Provider = "ollama", Model = "nomic-embed-text", Dimensions = 3 },
        },
        Pricing = { ["claude-opus-5-5"] = new ModelPricing { InputPerMTok = 4m, OutputPerMTok = 20m } },
    };

    private TenantRateLimiter? _limiter;

    public void Dispose() => _limiter?.Dispose();

    [Fact]
    public async Task A_turn_is_routed_rendered_redacted_priced_and_recorded()
    {
        var conversation = new AiConversation();
        conversation.AddUser(new TextPart("Customer jane@example.test called."), new UntrustedTextPart("description", "Mail me at jane@example.test"));

        var result = await Gateway().CompleteAsync(Request(conversation), TestContext.Current.CancellationToken);

        result.Stop.ShouldBe(AiStopKind.Completed);
        result.StructuredOutput!.Value.GetProperty("decision").GetString().ShouldBe("Approve");
        result.Usage.EstimatedCost.ShouldBe((1000 * 4m + 200 * 20m) / 1_000_000m);
        var sent = _anthropic.Requests.ShouldHaveSingleItem();
        sent.SystemPrompt.ShouldBe("Decide claims for WarrantyOS.");
        sent.MaxTokens.ShouldBe(16_000);
        sent.Route.Profile.SupportsEffort.ShouldBeTrue();
        sent.Messages[0].Parts.ShouldBe([new TextPart("Customer [EMAIL] called."), new UntrustedTextPart("description", "Mail me at [EMAIL]")]);
        conversation.Messages[0].Parts[0].ShouldBe(new TextPart("Customer jane@example.test called."));
        _aiOps.Received(1).AddModelCall(Arg.Is<ModelCall>(c =>
            c.TenantId == Aurora && c.Route == "adjudication" && c.Provider == "anthropic" && c.Model == "claude-opus-5-5"
            && c.PromptId == "decision" && c.PromptVersion == "1" && c.Status == ModelCallStatus.Ok && c.StopReason == "completed"
            && c.InputTokens == 1000 && c.OutputTokens == 200));
    }

    [Fact]
    public async Task Replay_mode_answers_every_chat_route_from_the_replay_provider()
    {
        _options.Mode = AiGatewayOptions.ReplayMode;

        await Gateway().CompleteAsync(Request(), TestContext.Current.CancellationToken);

        _replay.Requests.ShouldHaveSingleItem();
        _anthropic.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Output_that_breaks_the_schema_is_an_invalid_output_failure_with_the_errors()
    {
        _anthropic.Next = _ => Task.FromResult(Completed("""{"decision":"Maybe"}"""));

        var result = await Gateway().CompleteAsync(Request(), TestContext.Current.CancellationToken);

        result.Stop.ShouldBe(AiStopKind.Failed);
        result.StructuredOutput.ShouldBeNull();
        result.Failure!.Kind.ShouldBe(AiFailureKind.InvalidOutput);
        result.Failure.ValidationErrors!.ShouldNotBeEmpty();
        result.AssistantMessage.Parts.ShouldNotBeEmpty();
        _aiOps.Received(1).AddModelCall(Arg.Is<ModelCall>(c => c.Status == ModelCallStatus.Invalid));
    }

    [Fact]
    public async Task A_completed_turn_without_the_requested_structured_output_is_invalid()
    {
        _anthropic.Next = _ => Task.FromResult(Completed(null));

        var result = await Gateway().CompleteAsync(Request(), TestContext.Current.CancellationToken);

        result.Failure!.Kind.ShouldBe(AiFailureKind.InvalidOutput);
    }

    [Fact]
    public async Task A_slow_provider_is_a_timeout_failure_after_the_longer_of_the_request_and_route_timeouts()
    {
        _anthropic.Next = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Completed("{}");
        };
        _options.Routes["adjudication"].TimeoutSeconds = 1;
        var started = Stopwatch.GetTimestamp();

        var result = await Gateway().CompleteAsync(Request(timeout: TimeSpan.FromMilliseconds(50)), TestContext.Current.CancellationToken);

        Stopwatch.GetElapsedTime(started).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
        result.Stop.ShouldBe(AiStopKind.Failed);
        result.Failure!.Kind.ShouldBe(AiFailureKind.Timeout);
        _aiOps.Received(1).AddModelCall(Arg.Is<ModelCall>(c => c.Status == ModelCallStatus.Timeout));
    }

    [Fact]
    public async Task An_unexpected_provider_exception_is_a_provider_error_failure()
    {
        _anthropic.Next = _ => throw new HttpRequestException("connection reset");

        var result = await Gateway().CompleteAsync(Request(), TestContext.Current.CancellationToken);

        result.Failure!.Kind.ShouldBe(AiFailureKind.ProviderError);
        _aiOps.Received(1).AddModelCall(Arg.Is<ModelCall>(c => c.Status == ModelCallStatus.Error));
    }

    [Fact]
    public async Task Calls_beyond_the_tenant_rate_limit_fail_as_rate_limited()
    {
        _options.RateLimits.PerTenantRequestsPerMinute = 1;
        var gateway = Gateway();

        (await gateway.CompleteAsync(Request(timeout: TimeSpan.FromMilliseconds(100)), TestContext.Current.CancellationToken)).Stop
            .ShouldBe(AiStopKind.Completed);
        var limited = await gateway.CompleteAsync(Request(timeout: TimeSpan.FromMilliseconds(100)), TestContext.Current.CancellationToken);

        limited.Failure!.Kind.ShouldBe(AiFailureKind.RateLimited);
        _anthropic.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Max_tokens_follow_the_override_but_never_exceed_the_model_profile()
    {
        var gateway = Gateway();

        await gateway.CompleteAsync(Request() with { MaxTokensOverride = 32_000 }, TestContext.Current.CancellationToken);
        await gateway.CompleteAsync(Request(route: "extraction", prompt: new PromptRef("extract", 1), schema: null), TestContext.Current.CancellationToken);

        _anthropic.Requests[0].MaxTokens.ShouldBe(32_000);
        _anthropic.Requests[1].MaxTokens.ShouldBe(64_000);
    }

    [Fact]
    public async Task The_result_reports_the_output_token_limit_the_turn_ran_with()
    {
        var gateway = Gateway();

        var routeDefault = await gateway.CompleteAsync(Request(), TestContext.Current.CancellationToken);
        var overridden = await gateway.CompleteAsync(Request() with { MaxTokensOverride = 32_000 }, TestContext.Current.CancellationToken);

        routeDefault.MaxTokens.ShouldBe(16_000);
        overridden.MaxTokens.ShouldBe(32_000);
    }

    [Fact]
    public async Task Calls_for_another_tenant_or_with_a_mismatched_prompt_or_route_are_programming_errors()
    {
        var gateway = Gateway();
        var ct = TestContext.Current.CancellationToken;

        await Should.ThrowAsync<InvalidOperationException>(() => gateway.CompleteAsync(
            Request() with { Context = new AiCallContext(Guid.NewGuid(), null, null, "decision", "c") }, ct));
        await Should.ThrowAsync<ArgumentException>(() => gateway.CompleteAsync(Request(route: "extraction"), ct));
        await Should.ThrowAsync<ArgumentException>(() => gateway.CompleteAsync(
            Request() with { OutputSchema = new AiOutputSchema("other-schema", Schema) }, ct));
        await Should.ThrowAsync<ArgumentException>(() => gateway.CompleteAsync(Request(route: "embedding"), ct));
        _anthropic.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Embeddings_are_redacted_dimension_checked_and_recorded()
    {
        var result = await Gateway().EmbedAsync(
            new AiEmbeddingRequest(Context(), ["policy text", "mail jane@example.test"]), TestContext.Current.CancellationToken);

        result.Vectors.Count.ShouldBe(2);
        result.Dimensions.ShouldBe(3);
        _embeddings.Inputs.ShouldBe(["policy text", "mail [EMAIL]"]);
        _aiOps.Received(1).AddModelCall(Arg.Is<ModelCall>(c => c.Route == "embedding" && c.Provider == "ollama" && c.Status == ModelCallStatus.Ok));
    }

    [Fact]
    public async Task Embeddings_of_the_wrong_dimension_fail_and_are_recorded_as_errors()
    {
        _options.Routes["embedding"].Dimensions = 768;

        await Should.ThrowAsync<InvalidOperationException>(() => Gateway().EmbedAsync(
            new AiEmbeddingRequest(Context(), ["policy text"]), TestContext.Current.CancellationToken));

        _aiOps.Received(1).AddModelCall(Arg.Is<ModelCall>(c => c.Status == ModelCallStatus.Error));
    }

    [Fact]
    public async Task Platform_embeddings_without_a_tenant_are_not_recorded()
    {
        await Gateway().EmbedAsync(
            new AiEmbeddingRequest(new AiCallContext(Guid.Empty, null, null, "ingestion", "c"), ["global text"]),
            TestContext.Current.CancellationToken);

        _aiOps.DidNotReceiveWithAnyArgs().AddModelCall(default!);
    }

    private AiGateway Gateway()
    {
        var options = Options.Create(_options);
        _limiter = new TenantRateLimiter(options);
        var prompts = new PromptTemplateRegistry(
        [
            ("decision.v1.md", "---\nid: decision\nversion: 1\nroute: adjudication\noutputSchema: decision-recommendation\n---\nDecide claims for {{platform}}."),
            ("extract.v1.md", "---\nid: extract\nversion: 1\nroute: extraction\n---\nExtract fields."),
        ]);
        return new AiGateway(
            new ModelProfileRegistry(options),
            prompts,
            [_anthropic, _replay],
            [_embeddings],
            new EmailRedactor(),
            _limiter,
            new UsageRecorder(_aiOps, options),
            new FakeTenantContext(Aurora),
            options,
            TimeProvider.System,
            NullLogger<AiGateway>.Instance);
    }

    private static AiCallContext Context() => new(Aurora, Guid.NewGuid(), Guid.NewGuid(), "decision", "corr-1");

    private static AiTurnRequest Request(
        AiConversation? conversation = null,
        TimeSpan? timeout = null,
        string route = "adjudication",
        PromptRef? prompt = null,
        AiOutputSchema? schema = null)
    {
        if (conversation is null)
        {
            conversation = new AiConversation();
            conversation.AddUser(new TextPart("Case facts."));
        }

        prompt ??= new PromptRef("decision", 1);
        var isDecision = prompt.Id == "decision";
        return new AiTurnRequest(
            Context(),
            route,
            prompt,
            isDecision ? new Dictionary<string, string> { ["platform"] = "WarrantyOS" } : new Dictionary<string, string>(),
            conversation,
            [],
            isDecision ? schema ?? new AiOutputSchema("decision-recommendation", Schema) : schema,
            timeout ?? TimeSpan.FromSeconds(5));
    }

    private static AiTurnResult Completed(string? json)
    {
        JsonElement? output = json is null ? null : JsonDocument.Parse(json).RootElement.Clone();
        return new AiTurnResult(
            AiStopKind.Completed,
            new AiMessage(AiRole.Assistant, [new TextPart(json ?? "no json")]),
            [],
            output,
            new AiUsage("anthropic", "claude-opus-5-5", 1000, 200, 0, 0, TimeSpan.Zero, 0m),
            null);
    }

    private sealed class FakeProvider(string name) : IModelProvider
    {
        public List<ResolvedTurnRequest> Requests { get; } = [];

        public Func<CancellationToken, Task<AiTurnResult>> Next { get; set; } = _ => Task.FromResult(Completed("""{"decision":"Approve"}"""));

        public string Name => name;

        public Task<AiTurnResult> CompleteAsync(ResolvedTurnRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Next(ct);
        }
    }

    private sealed class EmailRedactor : IPiiRedactor
    {
        public RedactionResult Redact(string text)
        {
            var redacted = text.Replace("jane@example.test", "[EMAIL]", StringComparison.Ordinal);
            return new RedactionResult(redacted, redacted == text ? 0 : 1);
        }
    }

    private sealed class FakeEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<string> Inputs { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            Inputs.AddRange(values);
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                Inputs.Select(_ => new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f })))
            {
                Usage = new UsageDetails { InputTokenCount = 12 },
            });
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
