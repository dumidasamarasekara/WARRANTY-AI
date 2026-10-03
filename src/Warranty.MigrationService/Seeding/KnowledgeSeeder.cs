using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Storage;

namespace Warranty.MigrationService.Seeding;

/// <summary>
/// Stores each knowledge source in the <c>knowledge-sources</c> container and indexes it through
/// <see cref="IKnowledgeIngestor"/> under the caller's context: global sources with no tenant in
/// scope, a tenant's policies inside that tenant's scope. Unchanged sources are skipped by checksum.
/// </summary>
internal sealed class KnowledgeSeeder(IDocumentStore documents, IKnowledgeIngestor ingestor, ILogger<KnowledgeSeeder> logger)
{
    public async Task IngestAsync(IReadOnlyList<KnowledgeSeed> sources, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sources);
        foreach (var source in sources)
        {
            await documents.UploadKnowledgeSourceAsync(source.BlobPath, source.Content, ct);
            var result = await ingestor.IngestAsync(source.ToDocument(), ct);
            logger.LogInformation(
                "{Outcome} {Source} into {Namespace} ({Chunks} chunks)",
                result.Skipped ? "Unchanged" : "Indexed", source.BlobPath, result.Namespace, result.ChunkCount);
        }
    }
}
