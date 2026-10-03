# Contract: Tenant-Aware RAG

**Owner project**: `Warranty.Knowledge` · **Ports declared in**:
`Warranty.Application/Abstractions/Knowledge` · **Related**: [research.md](../research.md) R5–R7,
[data-model.md](../data-model.md) (knowledge database)

## Knowledge tiers

| Tier | Namespace | Storage | Contents (PoC) | Who can read |
|------|-----------|---------|----------------|--------------|
| Global | `global` | `knowledge_chunks` partition `global` | Terminology, generic repair concepts, generic fraud patterns, injection phrase list, procedures | Any tenant context |
| Tenant | `tenant-{slug}` | one partition per tenant | Warranty policies (all versions); extensible to manuals, specs, repair/replacement/pricing/regional/service-level rules | Only that tenant's context, subject to `allowed_roles` and `classification` |
| Case | — (not vector-indexed in PoC) | transactional DB + blobs | Claim facts, evidence, extracted data, claim history | Only the run for that claim (`ICaseKnowledgeProvider`) |

## Ports

```csharp
public interface IKnowledgeRetriever
{
    // Version-correct policy retrieval for a claim (filter-first, rank-second).
    Task<RetrievalResult> RetrievePolicyClausesAsync(PolicyRetrievalQuery query, CancellationToken ct);

    // Free-text search used by agent tools; still filtered and namespace-scoped.
    Task<RetrievalResult> SearchAsync(KnowledgeSearchQuery query, CancellationToken ct);
}

public sealed record PolicyRetrievalQuery(
    string QueryText,              // built by the harness from the extracted problem, not raw claimant text
    string ProductCategory,
    string? ProductModel,
    Region Region,
    DateOnly PurchaseDate,         // selects the policy version (clarification Q1)
    int TopK = 8);

public sealed record KnowledgeSearchQuery(
    string QueryText,
    KnowledgeScope Scope,          // Global | Tenant | GlobalAndTenant
    IReadOnlyList<DocumentType>? DocumentTypes,
    PolicyApplicability? Applicability, // product/region/purchase date filters, when searching policies
    int TopK = 5);

public sealed record RetrievalResult(
    IReadOnlyList<RetrievedChunk> Chunks,
    RetrievalOutcome Outcome);     // Ok | NoApplicablePolicy | AmbiguousPolicyVersion

public sealed record RetrievedChunk(
    Guid ChunkId, string Namespace, Guid DocumentId, string DocumentTitle, int Version,
    string? ClauseKey, string? SectionTitle, string Text,
    DateOnly? EffectiveFrom, DateOnly? EffectiveTo, double Score);

public interface IKnowledgeIngestor   // used by Warranty.MigrationService only
{
    Task<IngestionResult> IngestAsync(KnowledgeSourceDocument document, CancellationToken ct);
}

public interface ICaseKnowledgeProvider
{
    Task<CaseContext> GetCaseContextAsync(Guid claimId, int round, CancellationToken ct);
}
```

**No method takes a tenant ID.** The retriever reads the namespace from `ITenantContext`
(`tenant-{slug}`) and the caller's roles from the principal (`adjudication-service` for the
worker). Calling any method without an established tenant context throws.

## Retrieval rules (in order)

1. **Scope**: namespaces = `{tenant-{slug}}` (+ `global` when the scope includes it). The DB
   session sets `app.kb_namespace`; RLS enforces it independently.
2. **Hard filters (SQL `WHERE`, before similarity)**: `document_type`; `product_category` = query
   category or NULL (all); `product_model` = query model or NULL; `regions` contains query region or
   is empty; `effective_from ≤ purchase_date` and (`effective_to` is NULL or `≥ purchase_date`);
   `classification` allowed for the principal; `allowed_roles && principal_roles`.
3. **Version check** (policy retrieval only): distinct matching policy versions per policy code —
   0 → `NoApplicablePolicy`; > 1 → `AmbiguousPolicyVersion`; both lead to human review.
4. **Ranking**: cosine similarity on the query embedding *within* the filtered set; top-k.
   Always include `Period` and `Exclusion` clauses of the selected version, even if they rank
   below top-k (they are small and decisive).
5. **Post-retrieval assertion**: every chunk's namespace ∈ allowed namespaces and tenant ID matches
   the context. Any violation → empty result, `RETRIEVAL_SCOPE_VIOLATION` security event, run
   routed to human review.
6. **Recording**: one `aiops.rag_queries` row per call (filters, namespaces, results, latency).
7. **Presentation to models**: chunks are rendered as `[POL-n] {clause_key} — {title}
   (v{version}, effective {from}–{to})\n{text}`; `POL-n` IDs are issued by the run's
   `ReferenceRegistry`. Global snippets are rendered as `[GLB-n]` and are not citable as policy.

## Ingestion (migration service)

- Source: Markdown files with front matter, one file per policy version:

```markdown
---
namespace: tenant-aurora
documentType: WarrantyPolicy
policyCode: AUR-WP
title: Aurora Limited Warranty
version: 2
effectiveFrom: 2026-07-01
effectiveTo: null
regions: [NA, EU]
productCategories: []
classification: Internal
allowedRoles: [adjudication-service, claims-reviewer, auditor]
terms:
  standardCoverageMonths: { NA: 12, EU: 24 }
  componentCoverageMonths: { battery: 12 }
  accidentalDamage: { covered: false, windowMonths: 0, maxIncidents: 0 }
  exclusions: [ACCIDENTAL_DAMAGE, LIQUID_DAMAGE, COSMETIC_DAMAGE, UNAUTHORIZED_REPAIR]
clauses:
  AUR-WP-1:   { type: Coverage }
  AUR-WP-2.1: { type: Period }
  AUR-WP-3.2: { type: Exclusion, exclusionCode: ACCIDENTAL_DAMAGE }
---
## AUR-WP-1 Coverage
...
## AUR-WP-3.2 Exclusions — accidental damage
...
```

- `clauses` declares every heading's clause type; an `Exclusion` clause must name an
  `exclusionCode` that is listed in `terms.exclusions`, otherwise ingestion fails (research R26).
  The clause type and exclusion code are stored on `policy.policy_clauses` and copied to the chunk
  metadata.

- Steps: validate front matter (namespace must equal `tenant-{slug}` of the tenant being seeded,
  or `global`) → upsert `policy.*` rows and structured `terms` → split at `##` clause headings
  (one clause per chunk; long clauses split at paragraphs with the clause key repeated) → embed via
  the gateway `embedding` route → write chunks into the namespace partition → skip unchanged files
  by checksum.
- Changing the embedding model requires a full re-index (model and dimension are stored per
  document).
