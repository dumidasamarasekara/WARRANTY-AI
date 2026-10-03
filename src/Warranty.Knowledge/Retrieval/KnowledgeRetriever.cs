using System.Text.Json;
using System.Text.Json.Serialization;
using Warranty.Application.Abstractions;
using Warranty.Application.Abstractions.AI;
using Warranty.Application.Abstractions.Audit;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Abstractions.Persistence;
using Warranty.Domain.AiOps;
using Warranty.Domain.Common;
using Warranty.Domain.Policies;

namespace Warranty.Knowledge.Retrieval;

/// <summary>
/// Filter-first, rank-second retrieval (contracts/rag.md, research R7). The namespace, roles and
/// readable classifications come from <see cref="ITenantContext"/> only; the store applies them as
/// hard SQL filters before cosine ranking. Policy retrieval selects exactly one version per policy
/// by purchase date (none → <see cref="RetrievalOutcome.NoApplicablePolicy"/>, several →
/// <see cref="RetrievalOutcome.AmbiguousPolicyVersion"/>) and always adds that version's
/// <c>Period</c> and <c>Exclusion</c> clauses. Every result passes the <see cref="NamespaceGuard"/>
/// assertion, which fails closed with a <c>RETRIEVAL_SCOPE_VIOLATION</c> security event. Each call
/// adds one <c>aiops.rag_queries</c> row to the caller's unit of work.
/// </summary>
public sealed class KnowledgeRetriever(
    IKnowledgeStore store,
    IPolicyRepository policies,
    IAiGateway gateway,
    ITenantContext tenantContext,
    IAiOpsRepository aiOps,
    ISecurityEventWriter securityEvents,
    IPiiRedactor redactor,
    TimeProvider time) : IKnowledgeRetriever
{
    /// <summary>Upper bound for top-k and for the always-included decisive clauses of one version.</summary>
    public const int MaxTopK = 50;

    private static readonly ClauseType[] DecisiveClauseTypes = [ClauseType.Period, ClauseType.Exclusion];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<RetrievalResult> RetrievePolicyClausesAsync(PolicyRetrievalQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        NamespaceGuard.RequireTenant(tenantContext);
        ValidateTopK(query.TopK);
        var call = Begin("policy", query.QueryText, query.TopK, query.Attribution);

        var filter = new KnowledgeFilter(
            NamespaceGuard.NamespacesFor(tenantContext, KnowledgeScope.Tenant),
            [DocumentType.WarrantyPolicy],
            new PolicyApplicability(query.ProductCategory, query.ProductModel, query.Region, query.PurchaseDate),
            NamespaceGuard.ClassificationsFor(tenantContext),
            [.. tenantContext.Roles]);

        var documents = await store.FindDocumentsAsync(filter, ct);
        if (documents.Any(d => !NamespaceGuard.IsAllowedNamespace(filter.Namespaces, d.Namespace)))
        {
            return await FailClosedAsync(call, filter, documents.Select(d => new { documentId = d.DocumentId, @namespace = d.Namespace }), ct);
        }

        var outcome = await VersionOutcomeAsync(documents, ct);
        if (outcome != RetrievalOutcome.Ok)
        {
            Record(call, filter, outcome, []);
            return RetrievalResult.Empty(outcome);
        }

        var selected = filter with { DocumentIds = documents.Select(d => d.DocumentId).ToList() };
        var embedding = await EmbedAsync(call, ct);
        var ranked = await store.SearchChunksAsync(selected, embedding, query.TopK, ct);
        var decisive = await store.SearchChunksAsync(selected with { ClauseTypes = DecisiveClauseTypes }, embedding, MaxTopK, ct);

        var rankedIds = ranked.Select(c => c.ChunkId).ToHashSet();
        var chunks = ranked.Concat(decisive.Where(c => !rankedIds.Contains(c.ChunkId))).ToList();
        return await CompleteAsync(call, filter, chunks, RetrievalOutcome.Ok, ct);
    }

    public async Task<RetrievalResult> SearchAsync(KnowledgeSearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        NamespaceGuard.RequireTenant(tenantContext);
        ValidateTopK(query.TopK);
        var call = Begin("search", query.QueryText, query.TopK, query.Attribution);

        var filter = new KnowledgeFilter(
            NamespaceGuard.NamespacesFor(tenantContext, query.Scope),
            query.DocumentTypes,
            query.Applicability,
            NamespaceGuard.ClassificationsFor(tenantContext),
            [.. tenantContext.Roles]);

        var embedding = await EmbedAsync(call, ct);
        var chunks = await store.SearchChunksAsync(filter, embedding, query.TopK, ct);
        return await CompleteAsync(call, filter, chunks, RetrievalOutcome.Ok, ct);
    }

    private static void ValidateTopK(int topK)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(topK, MaxTopK);
    }

    /// <summary>
    /// Exactly one applicable version per policy is required. Versions are grouped by their policy;
    /// a document without a known policy version is its own group.
    /// </summary>
    private async Task<RetrievalOutcome> VersionOutcomeAsync(IReadOnlyList<KnowledgeDocumentMatch> documents, CancellationToken ct)
    {
        if (documents.Count == 0)
        {
            return RetrievalOutcome.NoApplicablePolicy;
        }

        var policyOfVersion = (await policies.GetVersionsAsync(ct)).ToDictionary(v => v.Id, v => v.PolicyId);
        var ambiguous = documents
            .GroupBy(d => d.PolicyVersionId is { } versionId && policyOfVersion.TryGetValue(versionId, out var policyId)
                ? policyId
                : d.DocumentId)
            .Any(group => group.Select(d => d.DocumentId).Distinct().Count() > 1);
        return ambiguous ? RetrievalOutcome.AmbiguousPolicyVersion : RetrievalOutcome.Ok;
    }

    private RetrievalCall Begin(string operation, string queryText, int topK, RetrievalAttribution? attribution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryText);
        return new RetrievalCall(
            operation,
            redactor.Redact(queryText).Text,
            topK,
            attribution?.Agent ?? tenantContext.PrincipalId,
            attribution,
            time.GetUtcNow(),
            time.GetTimestamp());
    }

    /// <summary>Embeds the redacted query text through the gateway's <c>embedding</c> route.</summary>
    private async Task<ReadOnlyMemory<float>> EmbedAsync(RetrievalCall call, CancellationToken ct)
    {
        var result = await gateway.EmbedAsync(
            new AiEmbeddingRequest(
                new AiCallContext(
                    tenantContext.TenantId, call.Attribution?.ClaimId, call.Attribution?.RunId, call.Agent, tenantContext.CorrelationId),
                [call.QueryText]),
            ct);
        return result.Vectors.Count == 1
            ? result.Vectors[0]
            : throw new InvalidOperationException($"The embedding route returned {result.Vectors.Count} vectors for one query.");
    }

    /// <summary>Runs the post-retrieval assertion, then records the query.</summary>
    private async Task<RetrievalResult> CompleteAsync(
        RetrievalCall call, KnowledgeFilter filter, IReadOnlyList<RetrievedChunk> chunks, RetrievalOutcome outcome, CancellationToken ct)
    {
        var violations = NamespaceGuard.Violations(tenantContext, filter.Namespaces, chunks);
        if (violations.Count > 0)
        {
            return await FailClosedAsync(
                call, filter, violations.Select(c => new { chunkId = c.ChunkId, @namespace = c.Namespace, tenantId = c.TenantId }), ct);
        }

        Record(call, filter, outcome, chunks);
        return new RetrievalResult(chunks, outcome);
    }

    private async Task<RetrievalResult> FailClosedAsync(RetrievalCall call, KnowledgeFilter filter, IEnumerable<object> offending, CancellationToken ct)
    {
        await securityEvents.RecordAsync(
            SecurityEventKind.RetrievalScopeViolation,
            tenantContext.PrincipalId,
            call.Attribution?.RunId is { } runId ? $"run:{runId}" : $"retrieval:{call.Operation}",
            new { agent = call.Agent, allowedNamespaces = filter.Namespaces, offending = offending.ToList() },
            ct);
        Record(call, filter, RetrievalOutcome.ScopeViolation, []);
        return RetrievalResult.Empty(RetrievalOutcome.ScopeViolation);
    }

    private void Record(RetrievalCall call, KnowledgeFilter filter, RetrievalOutcome outcome, IReadOnlyList<RetrievedChunk> chunks)
    {
        var filters = new
        {
            operation = call.Operation,
            outcome,
            documentTypes = filter.DocumentTypes,
            productCategory = filter.Applicability?.ProductCategory,
            productModel = filter.Applicability?.ProductModel,
            region = filter.Applicability?.Region,
            purchaseDate = filter.Applicability?.PurchaseDate,
            classifications = filter.Classifications,
            roles = filter.Roles,
        };

        aiOps.AddRagQuery(new RagQuery
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantContext.TenantId,
            RunId = call.Attribution?.RunId,
            Agent = call.Agent,
            Namespaces = filter.Namespaces,
            FiltersJson = JsonSerializer.Serialize(filters, JsonOptions),
            QueryText = call.QueryText,
            TopK = call.TopK,
            ResultsJson = JsonSerializer.Serialize(chunks.Select(c => new { chunkId = c.ChunkId, clauseKey = c.ClauseKey, score = c.Score }), JsonOptions),
            LatencyMs = (long)time.GetElapsedTime(call.StartTimestamp).TotalMilliseconds,
            StartedAt = call.StartedAt,
        });
    }

    private sealed record RetrievalCall(
        string Operation,
        string QueryText,
        int TopK,
        string Agent,
        RetrievalAttribution? Attribution,
        DateTimeOffset StartedAt,
        long StartTimestamp);
}
