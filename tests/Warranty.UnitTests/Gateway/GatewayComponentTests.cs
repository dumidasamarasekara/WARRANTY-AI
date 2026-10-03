using Microsoft.Extensions.Options;
using NSubstitute;
using Warranty.AI.Gateway.Prompts;
using Warranty.AI.Gateway.RateLimiting;
using Warranty.AI.Gateway.Routing;
using Warranty.AI.Gateway.Usage;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.AiOps;

namespace Warranty.UnitTests.Gateway;

public sealed class GatewayComponentTests
{
    private const string Decision = """
        ---
        id: decision
        version: 2
        route: adjudication
        outputSchema: decision-recommendation
        ---
        You adjudicate warranty claims for {{platform}}.
        """;

    [Fact]
    public void Templates_parse_front_matter_and_render_platform_variables()
    {
        var registry = new PromptTemplateRegistry([("decision.v2.md", Decision)]);

        var template = registry.Get(new PromptRef("decision", 2));

        template.Route.ShouldBe("adjudication");
        template.OutputSchema.ShouldBe("decision-recommendation");
        template.Render(new Dictionary<string, string> { ["platform"] = "WarrantyOS" })
            .ShouldBe("You adjudicate warranty claims for WarrantyOS.");
        Should.Throw<ArgumentException>(() => registry.Get(new PromptRef("decision", 1)));
    }

    [Fact]
    public void Rendering_fails_on_missing_or_unused_variables()
    {
        var template = new PromptTemplateRegistry([("decision.v2.md", Decision)]).Get(new PromptRef("decision", 2));

        Should.Throw<InvalidOperationException>(() => template.Render(new Dictionary<string, string>()));
        Should.Throw<InvalidOperationException>(() => template.Render(
            new Dictionary<string, string> { ["platform"] = "x", ["claimant"] = "y" }));
    }

    [Theory]
    [InlineData("decision.v3.md")]
    [InlineData("Decision.v2.md")]
    [InlineData("decision.md")]
    public void Templates_whose_file_name_disagrees_with_the_front_matter_are_rejected(string fileName)
        => Should.Throw<FormatException>(() => new PromptTemplateRegistry([(fileName, Decision)]));

    [Fact]
    public void Templates_need_front_matter_a_route_and_a_body_and_unique_versions()
    {
        Should.Throw<FormatException>(() => new PromptTemplateRegistry([("decision.v2.md", "no front matter")]));
        Should.Throw<FormatException>(() => new PromptTemplateRegistry([("decision.v2.md", "---\nid: decision\nversion: 2\n---\nBody")]));
        Should.Throw<FormatException>(() => new PromptTemplateRegistry([("decision.v2.md", "---\nid: decision\nversion: 2\nroute: x\n---\n")]));
        Should.Throw<InvalidOperationException>(() => new PromptTemplateRegistry([("decision.v2.md", Decision), ("decision.v2.md", Decision)]));
    }

    [Fact]
    public void The_embedded_templates_all_parse()
        => Should.NotThrow(PromptTemplateRegistry.FromEmbeddedResources);

    [Fact]
    public void Profiles_have_built_in_defaults_and_configuration_overrides_discovery()
    {
        var options = Options.Create(new AiGatewayOptions
        {
            Routes = { ["extraction"] = new RouteOptions { Provider = "anthropic", Model = "claude-haiku-4-5" } },
            ModelProfiles = { ["claude-opus-5-5"] = new ModelProfileOptions { MaxOutputTokens = 32_000 } },
        });
        var registry = new ModelProfileRegistry(options);

        var haiku = registry.Resolve("extraction").Profile;
        haiku.SupportsEffort.ShouldBeFalse();
        haiku.AdaptiveThinking.ShouldBeFalse();
        haiku.ContextWindow.ShouldBe(200_000);
        var opus = registry.Get("claude-opus-5-5");
        opus.SupportsEffort.ShouldBeTrue();
        opus.MaxOutputTokens.ShouldBe(32_000);

        registry.Update(opus with { MaxOutputTokens = 128_000, ContextWindow = 500_000 });
        registry.Get("claude-opus-5-5").MaxOutputTokens.ShouldBe(32_000);
        registry.Get("claude-opus-5-5").ContextWindow.ShouldBe(500_000);

        registry.Get("some-new-model").ShouldBe(ModelProfile.Unknown("some-new-model"));
        Should.Throw<ArgumentException>(() => registry.Resolve("unknown-route"));
    }

    [Fact]
    public void Cost_uses_configured_prices_and_standard_cache_multipliers_otherwise()
    {
        var recorder = new UsageRecorder(Substitute.For<IAiOpsRepository>(), Options.Create(new AiGatewayOptions
        {
            Pricing =
            {
                ["claude-opus-5-5"] = new ModelPricing { InputPerMTok = 4m, OutputPerMTok = 20m, CacheReadPerMTok = 0.2m },
                ["claude-haiku-4-5"] = new ModelPricing { InputPerMTok = 1m, OutputPerMTok = 5m },
            },
        }));

        recorder.EstimateCost("claude-opus-5-5", 1_000_000, 100_000, 500_000, 0).ShouldBe(4m + 2m + 0.1m);
        recorder.EstimateCost("claude-haiku-4-5", 0, 0, 1_000_000, 1_000_000).ShouldBe(0.1m + 1.25m);
        recorder.EstimateCost("nomic-embed-text", 1_000_000, 0, 0, 0).ShouldBe(0m);
    }

    [Fact]
    public void Model_calls_are_recorded_for_the_calling_tenant_only()
    {
        var aiOps = Substitute.For<IAiOpsRepository>();
        var recorder = new UsageRecorder(aiOps, Options.Create(new AiGatewayOptions()));
        var tenant = Guid.NewGuid();
        var claim = Guid.NewGuid();
        var usage = new AiUsage("anthropic", "claude-opus-5-5", 10, 20, 3, 4, TimeSpan.FromMilliseconds(1500), 0.25m);
        var started = DateTimeOffset.UnixEpoch;

        recorder.Record(new AiCallContext(Guid.Empty, null, null, "ingestion", "c-0"), "embedding", null, usage, ModelCallStatus.Ok, null, null, started);
        recorder.Record(new AiCallContext(tenant, claim, null, "decision", "c-1"), "adjudication", new PromptRef("decision", 2), usage,
            ModelCallStatus.Ok, "completed", null, started);

        aiOps.Received(1).AddModelCall(Arg.Is<ModelCall>(c =>
            c.TenantId == tenant && c.ClaimId == claim && c.Agent == "decision" && c.Route == "adjudication"
            && c.PromptId == "decision" && c.PromptVersion == "2" && c.InputTokens == 10 && c.OutputTokens == 20
            && c.CacheReadTokens == 3 && c.CacheWriteTokens == 4 && c.LatencyMs == 1500 && c.EstimatedCost == 0.25m
            && c.StopReason == "completed" && c.CorrelationId == "c-1" && c.StartedAt == started));
    }

    [Fact]
    public async Task Each_tenant_has_its_own_request_budget()
    {
        using var limiter = new TenantRateLimiter(Options.Create(new AiGatewayOptions { RateLimits = { PerTenantRequestsPerMinute = 2 } }));
        var aurora = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        (await limiter.TryAcquireAsync(aurora, TimeSpan.FromMilliseconds(50), ct)).ShouldBeTrue();
        (await limiter.TryAcquireAsync(aurora, TimeSpan.FromMilliseconds(50), ct)).ShouldBeTrue();
        (await limiter.TryAcquireAsync(aurora, TimeSpan.FromMilliseconds(50), ct)).ShouldBeFalse();
        (await limiter.TryAcquireAsync(Guid.NewGuid(), TimeSpan.FromMilliseconds(50), ct)).ShouldBeTrue();
    }
}
