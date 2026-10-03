using System.Text;
using Microsoft.Extensions.AI;

namespace Warranty.AI.Gateway.Providers.Replay;

/// <summary>
/// Deterministic embeddings for tests: each lower-cased word is hashed (FNV-1a) into one of
/// <see cref="Dimensions"/> buckets with a hash-derived sign, and the vector is L2-normalized. Texts
/// that share words therefore land close together, so filtered retrieval can be tested end to end
/// without a model. Empty text maps to a fixed unit vector.
/// </summary>
public sealed class HashEmbeddingGenerator(int dimensions = HashEmbeddingGenerator.DefaultDimensions)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    public const int DefaultDimensions = 768;

    public const string ModelId = "hash-embedding";

    public int Dimensions { get; } = dimensions > 0 ? dimensions : throw new ArgumentOutOfRangeException(nameof(dimensions));

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        long tokens = 0;
        var embeddings = values.Select(text =>
        {
            var (vector, count) = Embed(text);
            tokens += count;
            return new Embedding<float>(vector) { ModelId = ModelId };
        }).ToList();

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings)
        {
            Usage = new UsageDetails { InputTokenCount = tokens, TotalTokenCount = tokens },
        });
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return new EmbeddingGeneratorMetadata("hash", null, ModelId, Dimensions);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    private (float[] Vector, int Tokens) Embed(string text)
    {
        var vector = new float[Dimensions];
        var count = 0;
        foreach (var token in Tokens(text ?? string.Empty))
        {
            var hash = Fnv1a(token);
            vector[(int)(hash % (ulong)Dimensions)] += (hash >> 63) == 0 ? 1f : -1f;
            count++;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm == 0f)
        {
            vector[0] = 1f;
            return (vector, count);
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }

        return (vector, count);
    }

    private static IEnumerable<string> Tokens(string text)
    {
        var token = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                token.Append(char.ToLowerInvariant(c));
            }
            else if (token.Length > 0)
            {
                yield return token.ToString();
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }

    private static ulong Fnv1a(string token)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(token))
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }
}
