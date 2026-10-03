using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Warranty.AI.Gateway.Providers.Embeddings;

namespace Warranty.UnitTests.Gateway;

public sealed class OllamaEmbeddingProviderTests
{
    [Fact]
    public async Task Inputs_are_embedded_in_batches_with_usage_summed()
    {
        var inner = new FakeGenerator(dimensions: 3);
        using var provider = new OllamaEmbeddingProvider(inner, Options(batchSize: 2));

        var result = await provider.GenerateAsync(["a", "b", "c", "d", "e"], cancellationToken: TestContext.Current.CancellationToken);

        result.Count.ShouldBe(5);
        inner.Batches.Select(b => b.Count).ShouldBe([2, 2, 1]);
        inner.Batches.ShouldAllBe(b => b.ModelId == "nomic-embed-text");
        result.Usage!.InputTokenCount.ShouldBe(5);
        result[4].Vector.ToArray().ShouldBe([4f, 4f, 4f]);
    }

    [Fact]
    public async Task A_vector_of_the_wrong_dimension_is_rejected()
    {
        using var provider = new OllamaEmbeddingProvider(new FakeGenerator(dimensions: 4), Options());

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => provider.GenerateAsync(["a"], cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("4 dimensions; 3 are required");
    }

    [Fact]
    public async Task A_request_for_another_model_is_rejected()
    {
        using var provider = new OllamaEmbeddingProvider(new FakeGenerator(dimensions: 3), Options());

        await Should.ThrowAsync<InvalidOperationException>(() => provider.GenerateAsync(
            ["a"], new EmbeddingGenerationOptions { ModelId = "other-model" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_ollama_embed_api_is_called_on_the_configured_endpoint()
    {
        using var handler = new StubHandler("""{"model":"nomic-embed-text","embeddings":[[0.1,0.2,0.3],[0.4,0.5,0.6]],"prompt_eval_count":7}""");
        using var http = new HttpClient(handler);
        var options = OllamaEmbeddingOptions.FromConnectionString("Endpoint=http://ollama.test:11434;Model=nomic-embed-text");
        options.Dimensions = 3;
        using var provider = new OllamaEmbeddingProvider(http, options);

        var result = await provider.GenerateAsync(["first clause", "second clause"], cancellationToken: TestContext.Current.CancellationToken);

        result.Select(e => e.Vector.Length).ShouldBe([3, 3]);
        handler.Uri!.ToString().ShouldBe("http://ollama.test:11434/api/embed");
        handler.Body!["model"]!.GetValue<string>().ShouldBe("nomic-embed-text");
        handler.Body["input"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["first clause", "second clause"]);
    }

    [Fact]
    public void Connection_strings_supply_endpoint_and_model()
    {
        var options = OllamaEmbeddingOptions.FromConnectionString("Endpoint=http://localhost:11434;Model=mxbai-embed-large");

        options.Endpoint.ShouldBe(new Uri("http://localhost:11434"));
        options.Model.ShouldBe("mxbai-embed-large");
        options.Dimensions.ShouldBe(768);
        Should.Throw<FormatException>(() => OllamaEmbeddingOptions.FromConnectionString("Model=nomic-embed-text"));
    }

    private static OllamaEmbeddingOptions Options(int batchSize = 32) => new() { Dimensions = 3, BatchSize = batchSize };

    private sealed class FakeGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
    {
        private int _next;

        public List<(int Count, string? ModelId)> Batches { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var inputs = values.ToList();
            Batches.Add((inputs.Count, options?.ModelId));
            var embeddings = inputs.Select(_ =>
            {
                var value = _next++;
                return new Embedding<float>(Enumerable.Repeat((float)value, dimensions).ToArray());
            }).ToList();
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings)
            {
                Usage = new UsageDetails { InputTokenCount = inputs.Count },
            });
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        public JsonObject? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
