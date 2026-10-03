using System.Security.Cryptography;
using System.Text;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.Policies;

namespace Warranty.Knowledge.Ingestion;

/// <summary>
/// Indexes one knowledge source (contracts/rag.md, used by the migration service): parse and
/// validate the front matter → for a policy, create the <c>policy.*</c> rows with structured terms
/// and typed clauses → split at clause headings → embed through the gateway's <c>embedding</c> route
/// → write the chunks into the namespace partition. An unchanged source (same SHA-256) is skipped.
/// Published policy versions are immutable: changed content for an existing version is refused, so
/// earlier decisions keep citing the wording they were made on.
/// </summary>
public sealed class KnowledgeIngestor(
    IKnowledgeStore store,
    IPolicyRepository policies,
    IUnitOfWork unitOfWork,
    IAiGateway gateway,
    ITenantContext tenantContext,
    KnowledgeSourceValidator validator) : IKnowledgeIngestor
{
    /// <summary>The agent name recorded on the embedding call.</summary>
    public const string AgentName = "knowledge-ingestor";

    public async Task<IngestionResult> IngestAsync(KnowledgeSourceDocument document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        var source = FrontMatterParser.Parse(document);
        var isPolicy = source.DocumentType == DocumentType.WarrantyPolicy;
        var sections = ClauseChunker.Sections(source.Body, keyedHeadings: isPolicy);
        var checksum = Checksum(document.Content);

        var policy = isPolicy && source.PolicyCode is { } code ? await policies.FindPolicyByCodeAsync(code, ct) : null;
        var otherVersions = policy is null
            ? []
            : (await policies.GetVersionsAsync(ct)).Where(v => v.PolicyId == policy.Id).ToList();
        validator.Validate(source, sections, otherVersions);

        var version = isPolicy ? GetOrAddPolicyVersion(source, sections, policy, otherVersions, document.SourcePath, checksum) : null;
        var sourceRef = version is null ? $"source:{document.SourcePath.Replace('\\', '/')}" : $"policy_version:{version.Id}";

        if (await store.FindDocumentAsync(source.Namespace, sourceRef, ct) is { } indexed && indexed.Checksum == checksum)
        {
            return new IngestionResult(indexed.Id, source.Namespace, indexed.ChunkCount, Skipped: true);
        }

        var chunks = ClauseChunker.Chunk(sections);
        var embedded = await gateway.EmbedAsync(
            new AiEmbeddingRequest(
                new AiCallContext(
                    tenantContext.IsResolved ? tenantContext.TenantId : Guid.Empty,
                    null,
                    null,
                    AgentName,
                    tenantContext.IsResolved ? tenantContext.CorrelationId : Guid.CreateVersion7().ToString("N")),
                chunks.Select(c => c.EmbeddingInput).ToList()),
            ct);

        if (tenantContext.IsResolved)
        {
            // New policy rows and the embedding call's model-call row.
            await unitOfWork.SaveChangesAsync(ct);
        }

        var draft = new KnowledgeDocumentDraft(
            source.Namespace,
            source.DocumentType,
            sourceRef,
            source.Title,
            source.Version,
            source.ProductCategories.SingleOrDefault(),
            source.ProductModel,
            source.Regions,
            source.EffectiveFrom,
            source.EffectiveTo,
            source.Classification!.Value,
            source.AllowedRoles,
            embedded.Model,
            embedded.Dimensions,
            checksum);
        var chunkDrafts = chunks.Select((chunk, i) =>
        {
            var clause = chunk.ClauseKey is { } key && source.Clauses.TryGetValue(key, out var declared) ? declared : null;
            return new KnowledgeChunkDraft(
                chunk.Index, chunk.ClauseKey, chunk.SectionTitle, chunk.Text, embedded.Vectors[i], version?.Id, clause?.Type, clause?.ExclusionCode);
        }).ToList();

        var documentId = await store.SaveDocumentAsync(draft, chunkDrafts, ct);
        return new IngestionResult(documentId, source.Namespace, chunkDrafts.Count, Skipped: false);
    }

    /// <summary>The stored version for this source, or a new version (and policy) with its clauses.</summary>
    private PolicyVersion GetOrAddPolicyVersion(
        KnowledgeSource source,
        IReadOnlyList<KnowledgeSection> sections,
        WarrantyPolicy? policy,
        IReadOnlyList<PolicyVersion> otherVersions,
        string sourcePath,
        string checksum)
    {
        if (otherVersions.FirstOrDefault(v => v.Version == source.Version) is { } existing)
        {
            return existing.Checksum == checksum
                ? existing
                : throw new InvalidKnowledgeSourceException(
                    source.SourcePath,
                    [$"version {source.Version} of {source.PolicyCode} was already ingested with different content; publish it as a new version."]);
        }

        if (policy is null)
        {
            policy = WarrantyPolicy.Create(Guid.CreateVersion7(), tenantContext.TenantId, source.PolicyCode!, source.Title);
            policies.AddPolicy(policy);
        }

        var version = PolicyVersion.Create(
            Guid.CreateVersion7(),
            tenantContext.TenantId,
            policy.Id,
            source.Version,
            source.EffectiveFrom!.Value,
            source.EffectiveTo,
            source.Regions,
            source.ProductCategories,
            source.Terms!,
            sourcePath,
            checksum);
        policies.AddVersion(version);

        foreach (var section in sections)
        {
            var clause = source.Clauses[section.ClauseKey!];
            policies.AddClause(PolicyClause.Create(
                Guid.CreateVersion7(), version, section.ClauseKey!, clause.Type, clause.ExclusionCode, section.Title!, section.Text));
        }

        return version;
    }

    private static string Checksum(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
