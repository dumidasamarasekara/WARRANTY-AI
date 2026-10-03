using System.Net;
using System.Text;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Warranty.AI.Gateway;
using Warranty.AI.Gateway.Providers;
using Warranty.AI.Gateway.Providers.Anthropic;
using Warranty.AI.Gateway.Providers.Embeddings;
using Warranty.AI.Gateway.Providers.Replay;
using Warranty.AI.Gateway.Routing;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Persistence;
using Warranty.UnitTests.Infrastructure;

namespace Warranty.UnitTests.Gateway;

public sealed class GatewayRegistrationTests
{
    private const string OllamaConnection = "Endpoint=http://ollama.test:11434;Model=nomic-embed-text";

    [Fact]
    public void The_api_configuration_is_valid_and_resolves_the_gateway_with_both_chat_providers()
    {
        using var provider = Build(ApiConfiguration());
        using var scope = provider.CreateScope();

        var options = provider.GetRequiredService<IOptions<AiGatewayOptions>>().Value;
        options.Mode.ShouldBe(AiGatewayOptions.LiveMode);
        options.Routes.Keys.ShouldBe(["extraction", "vision", "policy-reasoning", "adjudication", "embedding"], ignoreOrder: true);
        options.Anthropic.NoTraining.ShouldBeTrue();
        options.Pricing.Keys.ShouldBe(["claude-opus-5-5", "claude-haiku-4-5"], ignoreOrder: true);
        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());

        scope.ServiceProvider.GetRequiredService<IAiGateway>().ShouldNotBeNull();
        scope.ServiceProvider.GetServices<IModelProvider>().Select(p => p.Name)
            .ShouldBe([AnthropicModelProvider.ProviderName, ReplayModelProvider.ProviderName], ignoreOrder: true);
        provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>().ShouldBeOfType<OllamaEmbeddingProvider>();
        provider.GetServices<IHostedService>().ShouldContain(s => s is AnthropicModelProfileLoader);
    }

    [Fact]
    public void A_replay_host_needs_neither_an_api_key_nor_ollama()
    {
        using var provider = Build(ApiConfiguration(new()
        {
            ["AiGateway:Mode"] = AiGatewayOptions.ReplayMode,
            ["AiGateway:Routes:embedding:Provider"] = HashEmbeddingGenerator.ProviderName,
            ["ConnectionStrings:embeddings"] = null,
        }));
        using var scope = provider.CreateScope();

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
        scope.ServiceProvider.GetRequiredService<IAiGateway>().ShouldNotBeNull();
        provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()
            .ShouldBeOfType<HashEmbeddingGenerator>().Dimensions.ShouldBe(768);
    }

    [Fact]
    public void Startup_refuses_chat_routes_whose_provider_does_not_declare_no_training()
    {
        using var provider = Build(ApiConfiguration(new() { ["AiGateway:Anthropic:NoTraining"] = "false" }));

        var failure = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        foreach (var route in new[] { "extraction", "vision", "policy-reasoning", "adjudication" })
        {
            failure.Failures.ShouldContain(f => f.Contains($"Route '{route}' is refused", StringComparison.Ordinal) && f.Contains("NoTraining", StringComparison.Ordinal));
        }

        failure.Failures.ShouldNotContain(f => f.Contains("'embedding'", StringComparison.Ordinal));
        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AiGatewayOptions>>().Value);
    }

    [Theory]
    [InlineData("AiGateway:Routes:extraction:Provider", "openai", "Route 'extraction' is refused")]
    [InlineData("AiGateway:Mode", "record", "AiGateway:Mode 'record'")]
    [InlineData("AiGateway:Routes:vision:Effort", "extreme", "effort 'extreme'")]
    [InlineData("AiGateway:Routes:embedding:Provider", "voyage", "unknown provider 'voyage'")]
    [InlineData("AiGateway:Routes:adjudication:Model", "", "needs a Provider and a Model")]
    public void Startup_refuses_invalid_gateway_configuration(string key, string value, string expected)
    {
        using var provider = Build(ApiConfiguration(new() { [key] = value }));

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Failures.ShouldContain(f => f.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void The_ollama_generator_serves_the_routed_model_and_dimension()
    {
        var options = new AiGatewayOptions
        {
            Routes = { ["embedding"] = new RouteOptions { Provider = "ollama", Model = "nomic-embed-text", Dimensions = 768 } },
        };

        DependencyInjection.CreateEmbeddingGenerator(options, Configuration(new() { ["ConnectionStrings:embeddings"] = OllamaConnection }))
            .ShouldBeOfType<OllamaEmbeddingProvider>();
        Should.Throw<InvalidOperationException>(() => DependencyInjection.CreateEmbeddingGenerator(options, Configuration([])))
            .Message.ShouldContain("'embeddings'");
        Should.Throw<InvalidOperationException>(() => DependencyInjection.CreateEmbeddingGenerator(new AiGatewayOptions(), Configuration([])))
            .Message.ShouldContain("AiGateway:Routes:embedding");
    }

    [Fact]
    public async Task Live_startup_loads_model_capabilities_from_the_models_api_and_configuration_still_wins()
    {
        using var handler = new ModelsHandler();
        handler.Models["claude-haiku-4-5"] = """
            {"id":"claude-haiku-4-5","type":"model","display_name":"Claude Haiku 4.5","created_at":"2025-10-01T00:00:00Z",
             "max_input_tokens":180000,"max_tokens":32000,
             "capabilities":{"image_input":{"supported":true},"pdf_input":{"supported":false},
                             "structured_outputs":{"supported":false},"effort":{"supported":false}}}
            """;
        var options = Options.Create(new AiGatewayOptions
        {
            Routes =
            {
                ["extraction"] = new RouteOptions { Provider = "anthropic", Model = "claude-haiku-4-5" },
                ["adjudication"] = new RouteOptions { Provider = "anthropic", Model = "claude-opus-5-5" },
                ["embedding"] = new RouteOptions { Provider = "ollama", Model = "nomic-embed-text", Dimensions = 768 },
            },
            ModelProfiles = { ["claude-haiku-4-5"] = new ModelProfileOptions { MaxOutputTokens = 8_000 } },
        });
        var registry = new ModelProfileRegistry(options);
        var opusBefore = registry.Get("claude-opus-5-5");

        await Loader(handler, registry, options).StartAsync(TestContext.Current.CancellationToken);

        var haiku = registry.Get("claude-haiku-4-5");
        haiku.ContextWindow.ShouldBe(180_000);
        haiku.MaxOutputTokens.ShouldBe(8_000);
        haiku.SupportsImages.ShouldBeTrue();
        haiku.SupportsPdf.ShouldBeFalse();
        haiku.SupportsStructuredOutputs.ShouldBeFalse();
        haiku.SupportsEffort.ShouldBeFalse();

        // Opus is unknown to the stubbed API (404): the built-in profile stays and startup continues.
        registry.Get("claude-opus-5-5").ShouldBe(opusBefore);
        handler.Requested.ShouldBe(["/v1/models/claude-haiku-4-5", "/v1/models/claude-opus-5-5"], ignoreOrder: true);
    }

    [Fact]
    public async Task Replay_startup_does_not_call_the_models_api()
    {
        using var handler = new ModelsHandler();
        var options = Options.Create(new AiGatewayOptions
        {
            Mode = AiGatewayOptions.ReplayMode,
            Routes = { ["adjudication"] = new RouteOptions { Provider = "anthropic", Model = "claude-opus-5-5" } },
        });

        await Loader(handler, new ModelProfileRegistry(options), options).StartAsync(TestContext.Current.CancellationToken);

        handler.Requested.ShouldBeEmpty();
    }

    private static AnthropicModelProfileLoader Loader(ModelsHandler handler, ModelProfileRegistry registry, IOptions<AiGatewayOptions> options)
        => new(
            new AnthropicClient(new ClientOptions { HttpClient = new HttpClient(handler, disposeHandler: false), ApiKey = "test-key", MaxRetries = 0, BaseUrl = "https://anthropic.test" }),
            registry,
            options,
            NullLogger<AnthropicModelProfileLoader>.Instance);

    private static ServiceProvider Build(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ITenantContext>(_ => new FakeTenantContext(Guid.NewGuid()));
        services.AddScoped(_ => Substitute.For<IAiOpsRepository>());
        services.AddScoped(_ => Substitute.For<IClaimRepository>());
        services.AddWarrantyAiGateway(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>The API's own appsettings.json plus an Ollama connection string, with optional overrides.</summary>
    private static IConfiguration ApiConfiguration(Dictionary<string, string?>? overrides = null)
        => new ConfigurationBuilder()
            .AddJsonFile(ReplayPaths.Resolve("src/Warranty.Api/appsettings.json"), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:embeddings"] = OllamaConnection })
            .AddInMemoryCollection(overrides ?? [])
            .Build();

    private static IConfiguration Configuration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>A stub Models API: known models return their JSON, others 404.</summary>
    private sealed class ModelsHandler : HttpMessageHandler
    {
        private readonly List<string> _requested = [];

        public Dictionary<string, string> Models { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Requested => _requested;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            _requested.Add(path);
            var found = Models.TryGetValue(path[(path.LastIndexOf('/') + 1)..], out var json);
            return Task.FromResult(new HttpResponseMessage(found ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    found ? json! : """{"type":"error","error":{"type":"not_found_error","message":"model not found"}}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
