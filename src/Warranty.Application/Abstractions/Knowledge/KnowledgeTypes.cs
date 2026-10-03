using Warranty.Domain.Common;
using Warranty.Domain.Policies;

namespace Warranty.Application.Abstractions.Knowledge;

/// <summary>Knowledge document types (data-model.md knowledge_documents).</summary>
public enum DocumentType
{
    WarrantyPolicy,
    ProductManual,
    ProductSpec,
    RepairRule,
    ReplacementRule,
    PricingRule,
    RegionalPolicy,
    ServiceLevelRule,
    Terminology,
    FraudPattern,
    Procedure,
}

public enum DocumentClassification
{
    Public,
    Internal,
    Confidential,
}

/// <summary>Which namespaces a search may read; the tenant namespace always comes from the tenant context.</summary>
public enum KnowledgeScope
{
    Global,
    Tenant,
    GlobalAndTenant,
}

public enum RetrievalOutcome
{
    Ok,
    NoApplicablePolicy,
    AmbiguousPolicyVersion,

    /// <summary>
    /// The post-retrieval assertion found a chunk outside the allowed namespaces or tenant; the result
    /// is empty, a <c>RETRIEVAL_SCOPE_VIOLATION</c> security event is recorded and the run goes to human review.
    /// </summary>
    ScopeViolation,
}

/// <summary>Who retrieves, for the <c>aiops.rag_queries</c> row and the embedding call; never used for scoping.</summary>
public sealed record RetrievalAttribution(string Agent, Guid? RunId = null, Guid? ClaimId = null);

/// <summary>Version-correct policy retrieval for a claim (filter first, rank second).</summary>
public sealed record PolicyRetrievalQuery(
    string QueryText,
    string ProductCategory,
    string? ProductModel,
    Region Region,
    DateOnly PurchaseDate,
    int TopK = 8,
    RetrievalAttribution? Attribution = null);

/// <summary>
/// Product, region and purchase-date filters for a policy search. A document applies when its
/// category, model and regions are "all" or match; a null category, model or region here matches only
/// documents that apply to all of them. A null purchase date does not filter by effective dates.
/// </summary>
public sealed record PolicyApplicability(string? ProductCategory, string? ProductModel, Region? Region, DateOnly? PurchaseDate);

public sealed record KnowledgeSearchQuery(
    string QueryText,
    KnowledgeScope Scope,
    IReadOnlyList<DocumentType>? DocumentTypes,
    PolicyApplicability? Applicability,
    int TopK = 5,
    RetrievalAttribution? Attribution = null);

/// <summary>
/// A retrieved chunk. Beyond the contract snippet, policy chunks also carry their version, clause
/// type and exclusion code so references can be recorded and exclusions grounded (research R26).
/// </summary>
public sealed record RetrievedChunk(
    Guid ChunkId,
    string Namespace,
    Guid DocumentId,
    string DocumentTitle,
    int Version,
    string? ClauseKey,
    string? SectionTitle,
    string Text,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    double Score,
    Guid? TenantId = null,
    Guid? PolicyVersionId = null,
    ClauseType? ClauseType = null,
    ExclusionCode? ExclusionCode = null);

public sealed record RetrievalResult(IReadOnlyList<RetrievedChunk> Chunks, RetrievalOutcome Outcome)
{
    public static RetrievalResult Empty(RetrievalOutcome outcome) => new([], outcome);
}

/// <summary>A source document to ingest (Markdown with front matter, contracts/rag.md).</summary>
public sealed record KnowledgeSourceDocument(string SourcePath, string Content);

public sealed record IngestionResult(Guid DocumentId, string Namespace, int ChunkCount, bool Skipped);
