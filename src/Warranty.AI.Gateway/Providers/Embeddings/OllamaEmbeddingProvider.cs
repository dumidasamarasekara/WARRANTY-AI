using System.Data.Common;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Warranty.AI.Gateway.Providers.Embeddings;

/// <summary>Where and how the local embedding model is reached (research R5).</summary>
public sealed class OllamaEmbeddingOptions
{
    public const string DefaultModel = "nomic-embed-text";

    public const int DefaultDimensions = 768;

    /// <summary>The Ollama server, e.g. from the Aspire connection string <c>Endpoint=http://…;Model=…</c>.</summary>
    public Uri? Endpoint { get; set; }

    public string Model { get; set; } = DefaultModel;

    /// <summary>The vector size every embedding must have; it matches the <c>vector(768)</c> column.</summary>
    public int Dimensions { get; set; } = DefaultDimensions;

    /// <summary>Inputs sent per <c>/api/embed</c> request.</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>Reads an Aspire Ollama connection string (<c>Endpoint=…;Model=…</c>); missing keys keep their defaults.</summary>
    public static OllamaEmbeddingOptions FromConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var options = new OllamaEmbeddingOptions();

        if (!builder.TryGetValue("Endpoint", out var endpoint) || !Uri.TryCreate(endpoint?.ToString(), UriKind.Absolute, out var uri))
        {
            throw new FormatException("The Ollama connection string needs an absolute 'Endpoint'.");
        }

        options.Endpoint = uri;
        if (builder.TryGetValue("Model", out var model) && model?.ToString() is { Length: > 0 } name)
        {
            options.Model = name;
        }

        return options;
    }
}

/// <summary>
/// The <c>ollama</c> embedding provider (contracts/ai-gateway.md, research R5): an
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> over OllamaSharp for the local
/// <c>nomic-embed-text</c> model. It splits large inputs into batches, refuses to serve a different
/// model than configured, and checks every vector has the configured dimension so a wrong model can
/// never write mismatched vectors into the knowledge store.
/// </summary>
public sealed class OllamaEmbeddingProvider : DelegatingEmbeddingGenerator<string, Embedding<float>>
{
    public const string ProviderName = "ollama";

    private readonly OllamaEmbeddingOptions _options;

    /// <summary>Uses <paramref name="http"/> for the Ollama API; its base address defaults to the configured endpoint.</summary>
    public OllamaEmbeddingProvider(HttpClient http, OllamaEmbeddingOptions options)
        : this(new OllamaApiClient(WithEndpoint(http, options), options.Model), options)
    {
    }

    internal OllamaEmbeddingProvider(IEmbeddingGenerator<string, Embedding<float>> inner, OllamaEmbeddingOptions options)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BatchSize, 1);
        _options = options;
    }

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (options?.ModelId is { Length: > 0 } requested && requested != _options.Model)
        {
            throw new InvalidOperationException($"The embedding provider serves '{_options.Model}', not '{requested}'.");
        }

        var inputs = values.ToList();
        var batchOptions = options?.Clone() ?? new EmbeddingGenerationOptions();
        batchOptions.ModelId = _options.Model;

        var embeddings = new List<Embedding<float>>(inputs.Count);
        long inputTokens = 0;
        foreach (var batch in inputs.Chunk(_options.BatchSize))
        {
            var generated = await base.GenerateAsync(batch, batchOptions, cancellationToken);
            if (generated.Count != batch.Length)
            {
                throw new InvalidOperationException(
                    $"Embedding model '{_options.Model}' returned {generated.Count} vectors for {batch.Length} inputs.");
            }

            foreach (var embedding in generated)
            {
                if (embedding.Vector.Length != _options.Dimensions)
                {
                    throw new InvalidOperationException(
                        $"Embedding model '{_options.Model}' returned {embedding.Vector.Length} dimensions; {_options.Dimensions} are required.");
                }

                embeddings.Add(embedding);
            }

            inputTokens += generated.Usage?.InputTokenCount ?? 0;
        }

        return new GeneratedEmbeddings<Embedding<float>>(embeddings)
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, TotalTokenCount = inputTokens },
        };
    }

    private static HttpClient WithEndpoint(HttpClient http, OllamaEmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        if (http.BaseAddress is null)
        {
            http.BaseAddress = options.Endpoint ?? throw new InvalidOperationException("The Ollama endpoint is not configured.");
        }

        return http;
    }
}
