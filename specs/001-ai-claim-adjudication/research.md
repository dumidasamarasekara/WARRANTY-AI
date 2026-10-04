# Research & Decisions: AI-Powered Warranty Claim Adjudication PoC

**Feature**: `specs/001-ai-claim-adjudication` | **Date**: 2026-10-02 (R23–R30 added 2026-10-03) | **Plan**: [plan.md](./plan.md)

This document resolves every open technical question for the plan. Each entry records the
decision, why it was chosen, and the alternatives considered. Technology constraints given by the
user (.NET 10, ASP.NET Core, React + TypeScript + Vite, PostgreSQL, vector store with tenant
namespaces, object storage, provider-agnostic AI Gateway, OIDC + RBAC, Podman, .NET Aspire) are
treated as fixed inputs; the research below chooses *within* them.

---

## R1. Overall shape: modular monolith with an in-process worker

- **Decision**: One ASP.NET Core API host (`Warranty.Api`) containing REST endpoints and a hosted
  background worker that runs claim adjudication. Business capabilities live in separate class
  libraries with enforced dependency rules (see plan → Project Structure). A separate
  `Warranty.MigrationService` runs migrations, seeding and knowledge indexing, then exits.
- **Rationale**: A one-week PoC needs one deployable unit and one debugging surface, but the
  constitution (VI, VIII) needs clean seams. Class-library boundaries plus architecture tests give
  the seams without distributed-system overhead. The worker can later move to its own process
  because it only talks to the rest of the system through Application ports and the job table.
- **Alternatives considered**: Microservices per agent (rejected: network, deployment and tracing
  cost with no PoC benefit); a single project (rejected: no enforceable boundary between
  deterministic guardrails and AI code, which Principle II depends on).

## R2. Agent orchestration: a custom, deterministic AI Harness

- **Decision**: Build the AI Harness in-house (`Warranty.AI.Harness`). The harness runs a
  **fixed, code-defined workflow** — Intake → (Evidence ‖ Policy) → Decision (+ Risk capability) →
  Guardrails → Action/Escalation — and gives each agent a **bounded tool-use loop** (max 6 model
  turns) with a per-agent tool allow-list. Agents never call each other; the harness performs every
  hand-off by passing an `AdjudicationContext` (the shared execution state).
- **Rationale**: The user requires the harness to control the full lifecycle (context
  construction, tool permissions, validation, checkpoints, tracing). A thin, explicit harness makes
  each of those steps visible and testable, which is the point of the PoC ("not UI → LLM →
  response"). A fixed workflow also keeps adjudication reproducible; model autonomy is confined to
  *within* an agent step (which tools to call, how to reason), never to *which* business step
  runs next.
- **Alternatives considered**:
  - *Microsoft Agent Framework / Semantic Kernel*: capable, but adds a large abstraction layer
    whose planner/handoff semantics would compete with the guardrail pipeline. Kept as a future
    option: individual agents could be hosted on it behind the same `IAgent` contract.
  - *Anthropic SDK `BetaToolRunner`*: drives the tool loop automatically, but the harness must
    check permissions, inject tenant scope and trace every call; a manual loop gives that control
    directly (the skill guidance explicitly allows the manual loop for this reason).
  - *LLM-driven supervisor agent choosing next steps*: rejected — makes the business process
    non-deterministic and harder to audit (Principles II, III).

## R3. AI Gateway and provider abstraction

- **Decision**: Business code and the harness depend only on `IAiGateway` (declared in
  `Warranty.Application`), using provider-neutral request/response types. `Warranty.AI.Gateway`
  implements it with: model routing by *task* (not by model name), prompt-template registry,
  PII redaction, per-tenant rate limiting, token/cost accounting, request logging, and retry
  policy. Providers plug in behind an internal `IModelProvider` port:
  - `AnthropicModelProvider` — uses the **official Anthropic C# SDK** (NuGet `Anthropic`,
    `AnthropicClient`, `client.Messages.Create`, `client.Beta.Messages.Create`) directly, so
    structured outputs (`OutputConfig.Format = JsonOutputFormat`), effort, PDF document blocks,
    prompt caching and usage reporting are all available without lossy mapping.
  - `ScriptedModelProvider` / `ReplayModelProvider` — deterministic providers for tests and
    evaluation replays.
  - Future: a generic `ChatClientModelProvider` over `Microsoft.Extensions.AI.IChatClient` to add
    other vendors without touching the harness.
- **Rationale**: Principle VI forbids business logic depending on a vendor SDK. Keeping the
  official SDK inside a single adapter gives full feature access for the primary provider while an
  architecture test guarantees no other project references it.
- **Alternatives considered**: Using `IChatClient` as the *only* abstraction (rejected for the
  primary provider: effort, cache control and refusal-fallback settings would go through untyped
  additional-properties, making the most important calls the least type-safe); calling the REST
  API directly (rejected: the skill requires the official SDK when one exists).

## R4. Models and per-task routing (primary provider: Anthropic)

- **Decision** (configured in `appsettings` under `AiGateway:Routes`, never hard-coded in agents):

  | Task route | Used by | Model | Settings |
  |---|---|---|---|
  | `extraction` | Intake Agent (problem description), Evidence Agent (invoice PDF) | `claude-haiku-4-5` | No thinking/effort parameters (not supported on Haiku 4.5); structured output |
  | `vision` | Evidence Agent (damage photos) | `claude-opus-5-5` | Adaptive thinking (always on), `effort: medium`; structured output |
  | `policy-reasoning` | Policy Agent | `claude-opus-5-5` | `effort: medium`; tools; structured output |
  | `adjudication` | Decision Agent (+ risk capability) | `claude-opus-5-5` | `effort: high`; tools; structured output |
  | `embedding` | Knowledge ingestion & retrieval | Local embedding model (R5) | 768-dimension vectors |

- **Rationale**: The user asked for an inexpensive extraction model, a vision model and a
  reasoning model. `claude-haiku-4-5` ($1 / $5 per MTok) is the current low-cost tier and accepts
  images and PDFs; `claude-opus-5-5` ($4 / $20 per MTok) is the current default model and
  handles both photo analysis and adjudication. Routing by task means swapping a model is a config
  change (FR-042).
- **Provider-specific rules the adapter must honour** (from the current API reference):
  - Opus 5.5: thinking cannot be disabled (omit `Thinking` or send adaptive); effort defaults to
    `medium`, so set it explicitly; **forced `tool_choice` (`any`/`tool`) returns 400** — use
    `auto` with `strict: true` tools, and use structured outputs to get JSON; assistant prefill is
    not supported.
  - Thinking blocks returned during a tool loop must be echoed back unchanged and the conversation
    must stay **append-only** (no editing earlier turns).
  - Parallel tool calls: execute all, return all `tool_result` blocks in one user message.
  - Always check `stop_reason`: `refusal` and `max_tokens` are treated as an incomplete AI step
    (→ FR-031, human review), never parsed as a decision.
  - **Server-side refusal fallback is enabled by default** on Opus 5.5 calls
    (`fallbacks: "default"` with beta `server-side-fallback-2026-07-01`, beta messages endpoint).
    If a refusal still surfaces, the claim goes to human review.
  - Model capabilities (structured-output support per model, context window) are read from the
    Models API at startup and cached in model profiles; the gateway falls back to
    "schema in prompt + strict parse" if a routed model lacks native structured outputs. Either
    way the guardrail schema validation (R13) re-validates every result.
  - API-level `citations` are **not** used: they are incompatible with `output_config.format`.
    Citations are instead structured fields referencing harness-issued reference IDs (R7).
- **Alternatives considered**: One model for everything (simpler, but ignores the requested
  cost tiering); Sonnet for adjudication (the default model is Opus 5.5 and the user did not ask
  to downgrade the reasoning step).

## R5. Embeddings

- **Decision**: Generate embeddings with a **local open embedding model served by Ollama**
  (`nomic-embed-text`, 768 dimensions) running as a Podman container under Aspire, accessed
  through `Microsoft.Extensions.AI.IEmbeddingGenerator<string, Embedding<float>>` inside the
  gateway. Embedding model name and dimension are recorded on every indexed chunk.
- **Rationale**: The primary chat provider has no embeddings endpoint, so a second provider is
  needed anyway; a local model keeps tenant policy text on the machine, costs nothing for the PoC,
  and visibly demonstrates multi-provider routing through the gateway (Principle VI).
- **Alternatives considered**: Hosted embedding APIs (e.g., Voyage AI, Azure OpenAI) — better
  retrieval quality at scale; supported later by adding an `IEmbeddingGenerator` registration.
  Changing the embedding model requires re-indexing, which the migration service supports.

## R6. Vector store and namespaces

- **Decision**: **PostgreSQL + pgvector in a separate `knowledge` database** (same Postgres
  server container as the transactional `warranty` database, different database and role).
  `knowledge_chunks` is **LIST-partitioned by `namespace`** (`global`, `tenant-aurora`,
  `tenant-borealis`, …), each partition with its own HNSW (cosine) index. Row-Level Security
  restricts reads to `namespace = 'global' OR namespace = current_setting('app.kb_namespace')`.
  Access goes through `IKnowledgeStore` so a dedicated vector database can replace it.
- **Rationale**: Meets the "tenant namespace/index" requirement literally (one partition and index
  per namespace), keeps RAG data out of the transactional database, gives hard SQL metadata filters
  for versioning (R7), and adds database-enforced isolation instead of relying on application code
  or the LLM. One fewer infrastructure component than a separate vector database.
- **Alternatives considered**: Qdrant with a collection per namespace (good fit, Aspire
  integration exists; rejected for the PoC only to avoid another stateful service and because RLS
  gives an extra isolation layer); Azure AI Search / OpenSearch (production-grade options, out of
  PoC scope); one shared unpartitioned table with a `tenant_id` filter only (rejected: a single
  missing filter would leak data).

## R7. Policy versioning and effective-date retrieval

- **Decision**: Retrieval is **filter-first, rank-second**:
  1. Hard metadata filters (in SQL, before similarity): namespace from the tenant context;
     `document_type = WarrantyPolicy`; product category/model match (or "all products"); region
     match (or "all regions"); `effective_from ≤ purchase_date` and
     (`effective_to IS NULL` or `effective_to ≥ purchase_date`) — per the clarified spec, the
     **policy version is selected by purchase date** and the coverage window is evaluated against
     the claim date; `classification` and `allowed_roles` checked against the calling principal.
  2. Vector similarity ranking only *within* the filtered set (top-k = 8).
  3. Post-retrieval authorization assertion: every returned chunk must belong to `global` or the
     context tenant's namespace and pass the role check — otherwise the whole retrieval fails
     closed and a security event is recorded.
- Each policy version also carries **structured coverage terms** (coverage months per region,
  accidental-damage window, exclusion codes, component-specific periods) seeded from the policy
  file's front matter. Guardrails compute the coverage window from these terms deterministically
  (FR-025); the LLM never decides the date arithmetic.
- Policy chunks are cut at clause boundaries (one clause per chunk) and carry a stable
  `clause_key` (e.g., `AUR-WP-2.1`). The harness presents retrieved clauses to the model as
  `[POL-1]…[POL-n]` and evidence as `[EV-1]…[EV-n]`; the model cites those IDs; the harness maps
  them back. Any ID not issued for this run makes the recommendation invalid (FR-022) — the model
  never sees database IDs, so it cannot reference another tenant's content.
- **Rationale**: Semantic similarity alone would happily return the newest or most similar
  version; the user explicitly requires version-correct retrieval.
- **Alternatives considered**: Asking the LLM to pick the right version (rejected: Principle II);
  hybrid keyword + vector ranking (deferred: the filtered candidate set per tenant is small).

## R8. Transactional data isolation

- **Decision**: Shared database, shared schema, **`tenant_id` on every tenant-owned row**, with
  three layers:
  1. EF Core global query filters bound to the scoped `ITenantContext`.
  2. **PostgreSQL Row-Level Security** (`FORCE ROW LEVEL SECURITY`) on every tenant-owned table,
     keyed on `current_setting('app.tenant_id')`, set per connection by an EF Core connection
     interceptor from `ITenantContext`. The app connects as a non-owner role (`warranty_app`) so
     RLS cannot be bypassed.
  3. Repository/application checks that return **404 (not 403)** for cross-tenant IDs and record a
     security event (FR-005: no existence leak).
- Documented row-level-security exceptions (none returns tenant business data): the migration
  service (owner connection), `claims.dequeue_claim_job` (R12) and `audit.exists_in_other_tenant`
  (cross-tenant denial logging).
- Future strategies (schema-per-tenant, database-per-tenant) are enabled by resolving the
  connection through `ITenantDataSourceResolver` — PoC implementation returns the shared source.
- **Rationale**: The user asked for a practical isolation implementation, not per-tenant
  infrastructure, while Principle I demands that a single coding mistake cannot leak data. RLS
  makes the database itself refuse cross-tenant reads.
- **Alternatives considered**: Schema/database per tenant now (rejected for PoC: migration and
  provisioning overhead); query filters only (rejected: one `IgnoreQueryFilters()` or raw SQL call
  would bypass isolation).

## R9. Tenant resolution and propagation

- **Decision**: Tenant identity is established **only** from trusted context, in this order:
  - **Staff** (agents, reviewers, auditors): the `tenant_id` claim in the validated OIDC access
    token.
  - **Claimant channel** (no login): the request **Host** mapped through the server-side
    `tenant_channels` registry (e.g., `aurora.localhost` → Aurora). Locally the Vite dev proxy
    preserves the Host header.
  - **Background adjudication**: the tenant stored on the job record, which was written under an
    authenticated request; the worker opens a `TenantContextScope` as the system principal
    `adjudication-service` for that tenant.
- `tenantId` values in request bodies, query strings, or LLM output are **never** read. Tool
  schemas contain no tenant parameter; the harness injects scope.
- Pipeline: Authentication → Tenant Resolution → Authorization (role policies) → `ITenantContext`
  → Retrieval scope → Tool scope → Data access (RLS session variable).
- **Alternatives considered**: Tenant slug in the URL path (rejected: client-supplied); channel API
  key in a header (viable later for embedded widgets).

## R10. Identity provider and access for claimants

- **Decision**: **Keycloak** (container, Aspire Keycloak hosting integration, realm imported from
  `infra/keycloak/warranty-realm.json`). One realm; staff users carry a `tenant_id` attribute
  mapped into tokens and one or more realm roles `claims-agent`, `claims-reviewer`, `auditor`
  (separation of duties: R29). The SPA uses
  Authorization Code + PKCE; the API validates JWT bearer tokens.
- Claimants have no accounts (spec). After matching **claim reference + submitted email/phone**
  (FR-037a) on the tenant's channel, the API issues a **claim-scoped access token** (signed by the
  API, 30-minute lifetime, claims: tenant, claim ID, scope `claimant`). Failed matches return the
  same generic 401 whether or not the claim exists and write a security event. The endpoint is
  rate-limited (5 attempts / 15 minutes per IP + reference) — this closes the low-impact
  "rate limiting" item left open by `/speckit-clarify`.
- Claim references are random (10 characters, Crockford base32) so they cannot be enumerated.
- **Alternatives considered**: `dotnet user-jwts` dev tokens (rejected: not a real OIDC provider);
  Entra ID / Auth0 (production options; the API only depends on standard OIDC metadata).

## R11. Document / evidence storage

- **Decision**: **Azure Blob Storage API via the Azurite emulator** (Aspire Azure Storage
  integration in emulator mode), behind `IDocumentStore`. One container per tenant
  (`tenant-aurora`, …) plus `knowledge-sources`. Blob path:
  `claims/{claimId}/{round}/{evidenceId}{ext}`. Files are never exposed by public URL; the API
  streams them after an authorization + tenant check. A SHA-256 content hash of the sanitized file
  (below) is computed at upload for reused-evidence detection (FR-017/018, within tenant only), so
  a photo re-uploaded with different metadata still matches.
- **Upload sanitizing** (FR-006a, FR-009; checklist CHK010): `UploadSanitizer` runs before
  storage. It checks the file's magic bytes against the allowed types (the file name and declared
  content type are not trusted); re-encodes photos after applying the EXIF orientation, which
  drops all embedded metadata (EXIF incl. GPS location and device data, XMP, IPTC, comments); and
  rejects encrypted PDFs and PDFs containing JavaScript, launch actions or embedded files. Only
  the sanitized bytes are stored, hashed and sent to a model. Malware scanning is a production
  extension (see plan, PoC simplifications).
- Uploads: max 10 files per submission, 15 MB per file; allowed types JPEG, PNG, WebP and PDF.
  HEIC is rejected with a message asking for JPEG or PNG (conversion deferred). Photos are
  downscaled (long edge ≈1,500 px) by the AI Gateway before being sent to a model.
- **Alternatives considered**: MinIO (S3 API; community distribution changes make it a weaker
  default); local filesystem (no separation from transactional storage, no realistic SDK).

## R12. Background processing

- **Decision**: A PostgreSQL-backed job table (`claims.claim_jobs`) polled by a hosted
  `BackgroundService` using `SELECT … FOR UPDATE SKIP LOCKED`. Submission writes the claim and its
  job in one transaction and returns `202 Accepted`. Jobs are at-least-once; each adjudication run
  is keyed by `(claim_id, round)` so a retried job resumes or restarts the same run without
  duplicate side effects. Up to 2 automatic retries for infrastructure failures; AI failures follow
  FR-031 (route to human review) instead of retrying indefinitely.
- **Picking up jobs across tenants**: before claiming a job the worker has no tenant context.
  Dequeue goes through one `SECURITY DEFINER` function, `claims.dequeue_claim_job(worker_id,
  lock_seconds)`. It locks one job with `FOR UPDATE SKIP LOCKED` and returns only the job ID,
  tenant ID, claim ID, round and correlation ID. `warranty_app` may execute it but has no direct
  access to other tenants' rows. Everything after that runs inside the job's tenant context,
  under row-level security.
- **Alternatives considered**: In-memory `Channel<T>` (loses work on restart); Hangfire or a
  message broker (extra infrastructure for no PoC benefit).

## R13. Guardrails and the action boundary

- **Decision**: `Warranty.Guardrails` is a deterministic engine with **no reference to any AI
  package**. It runs, in order: (1) schema/structure validation of the AI result, (2) reference
  validation (every `[POL-n]`/`[EV-n]` was issued for this run), (3) business-rule checks
  (catalog membership, deterministic coverage window, claim value vs limit, tenant rules such as
  "always review" product categories), (4) confidence and risk thresholds, (5) conflict checks
  (AI vs deterministic results), (6) authorization (the acting principal and tenant may perform
  the action; auto-decisions allowed by tenant settings), (7) **action approval**.
- Only the engine can construct an `ApprovedAction` (internal constructor). The `ActionExecutor`
  — the single path to consequential operations (finalize approve/reject, request information,
  create repair request, notify customer) — accepts nothing else. Consequential tools are never
  in any LLM agent's tool allow-list. This makes "the LLM cannot trigger a business action" a
  compile-time property, not a convention.
- Escalation triggers implemented (user list ↔ spec): value above limit, confidence below minimum,
  evidence conflicts, ambiguous policy (coverage `UNDETERMINED`, no applicable policy, or more than
  one applicable version), risk medium/high (any risk signal, R23), tenant rule requiring human
  approval, invalid or unavailable AI output, AI recommends `HUMAN_REVIEW`, claim returned after a
  reviewer's information request, information still incomplete after two automatic requests
  (R24), unsafe claimant-facing text (R25).
- **Reconciliation with the spec**: the user's list says "required information is missing →
  escalate". Spec FR-010 / FR-029 (business behavior, which governs) say missing information
  pauses the claim in `PendingInformation` for the **submitter** — itself a human checkpoint.
  It is routed to a **reviewer** only when another escalation condition also applies, including
  the two-request limit (R24).

## R14. Prompt-injection and untrusted content

- **Decision**: Defense in depth: (a) all claimant-supplied text (description, invoice text,
  text read from images) is placed inside clearly delimited `<untrusted_claim_content>` blocks in
  the user turn and the system prompt states it is evidence only; (b) a deterministic detector
  (phrase/pattern list maintained in global knowledge) raises a `MANIPULATION_ATTEMPT` risk signal;
  (c) the Decision schema includes `manipulationDetected`; (d) LLM agents only hold read-only
  tools; (e) any manipulation signal, like every risk signal (R23), blocks automatic approval and
  rejection in the guardrail engine (FR-019, SC-010).

## R15. Privacy controls in the gateway

- **Decision**: The context builder sends models only what a step needs: customer name, email,
  phone and street address are replaced by placeholders (`[CUSTOMER]`, `[EMAIL]`, …); region and
  country are kept. A regex-based redactor additionally scrubs emails and phone numbers found in
  free text before it leaves the system. Prompts and responses are logged **after** redaction;
  the same redacted form is the only one written to traces, `aiops` call records and the decision
  trail (FR-006a).
  Photos are not redacted in the PoC (documented simplification). All PoC data is synthetic.
  Since 2026-10-03 this is a spec requirement (FR-006a); the checked guarantees and the provider
  no-training condition are in R28.
- **Alternatives considered**: An NER-based PII service (e.g., Presidio) — a later extension behind
  the same `IPiiRedactor` port.

## R16. Observability

- **Decision**: Two complementary layers:
  1. **OpenTelemetry** (via `Warranty.ServiceDefaults`) exporting traces, metrics and logs to the
     Aspire dashboard. Custom `ActivitySource`s for the harness, agents, tools, retrieval and the
     gateway, using GenAI semantic-convention attributes (`gen_ai.request.model`,
     `gen_ai.usage.input_tokens`, …) plus `warranty.tenant_id`, `warranty.claim_id`,
     `warranty.run_id`. The W3C trace ID doubles as the correlation ID and is returned in an
     `X-Correlation-Id` header.
  2. **Persisted AI execution records** (`aiops` schema: model calls, tool calls, RAG queries) and
     the immutable decision trail, which the UI renders as the decision trace. Telemetry backends
     are ephemeral; audit needs durable, tenant-scoped storage.
- Cost: token usage × a configurable price table (`AiGateway:Pricing`, seeded with current list
  prices: Opus 5.5 $4/$20, Haiku 4.5 $1/$5 per MTok input/output) stored per model call.
- Prompt caching: stable platform-owned content (tool definitions, system prompt, global knowledge)
  goes first with a cache breakpoint; tenant and case data come after it. Cache hits are verified
  via `usage.cache_read_input_tokens`. The cached prefix therefore never contains tenant data.

## R17. Audit trail integrity

- **Decision**: `audit.decision_trail_entries` is append-only: the app role has `INSERT`/`SELECT`
  only, a trigger rejects `UPDATE`/`DELETE`, and each entry stores `hash = SHA-256(prev_hash ‖
  canonical payload)` per claim, making tampering detectable. Corrections are new entries
  (FR-039). Security events go to `audit.security_events`.

## R18. Testing strategy and tools

- **Decision**:
  - .NET: **xUnit v3**, **Shouldly** assertions, **NSubstitute** for fakes, **NetArchTest** for
    dependency rules, **Testcontainers** (PostgreSQL+pgvector, Azurite) under Podman, and
    `WebApplicationFactory` for API tests with a test authentication handler.
  - AI determinism: `ReplayModelProvider` returns recorded model responses (fixtures under
    `tests/fixtures/ai-recordings/`), so full end-to-end adjudication scenarios run in CI without
    network calls or cost.
  - Frontend: **Vitest** + **React Testing Library** + **MSW**.
- **Alternatives considered**: FluentAssertions (v8 requires a commercial licence); Aspire
  `DistributedApplicationTestingBuilder` for every test (too slow; used only for one smoke test).

## R19. AI evaluation

- **Decision**: `tests/Warranty.Evaluation` is a separate console app — never referenced by
  production code — that runs the harness in-process against an isolated evaluation database and
  a golden dataset (`seed/golden/`, ≥20 labeled claims per tenant per SC-005). Two modes:
  `--replay` (deterministic, CI) and `--live` (real models, opt-in, costs money).
- **Golden set composition** (SC-005; checklist CHK029): per tenant at least 4 cases of each
  expected recommendation (`APPROVE`, `REJECT`, `REQUEST_MORE_INFORMATION`, `HUMAN_REVIEW`).
  Expected recommendation, disposition and cited clause keys are labeled by the case author from
  the tenant's seeded policy text before any model run, reviewed in the pull request that adds the
  case, and never copied from model output (a live run that disagrees is a finding, not a relabel).
- **Manipulation cases** (SC-010; checklist CHK030): English only; at least one case per placement
  — problem description (S8), invoice text (S8-invoice) and text visible in a photo (S8-photo).
- Metrics:
  extraction field accuracy, retrieval recall@k of expected clause keys, recommendation and
  disposition accuracy, confidence calibration (Brier score, accuracy by confidence band),
  unsupported-claim rate (citations not issued for the run, evidence IDs that don't exist), and
  escalation recall (no should-escalate case auto-finalized). Human override rate is reported from
  production review decisions (`review_decisions.overrides_ai`). Claim history is reset per case
  so golden cases don't trip duplicate/reuse risk signals on each other.

## R20. Frontend stack

- **Decision**: React 19 + TypeScript (strict) + Vite, React Router, TanStack Query,
  `oidc-client-ts` + `react-oidc-context` (Authorization Code + PKCE), a typed API client generated
  from `contracts/rest-api.openapi.yaml` with `openapi-typescript` + `openapi-fetch`. One SPA with
  two areas: the tenant-branded **claimant portal** (host-resolved tenant) and the **staff
  workspace** (claims, review queue, decision trace). Plain CSS modules; no component library.
- **Visual design (added 2026-10-03)**: the SPA implements the **WarrantyOS design system** from
  the Claude Design prototype in [design/](./design/), specified for this feature in
  [ui-design.md](./ui-design.md): design tokens as CSS custom properties, a small in-repo component
  kit under `src/web/src/shared/ui/`, and an AI / human / system visual language (violet dashed =
  AI recommends, blue solid = a person decides, grey = the system executes) that makes Principles
  III and IV visible. Fonts (Fraunces, Inter, JetBrains Mono) are self-hosted through `@fontsource`
  packages. Prototype screens without a requirement in this feature (dashboard, AI operations,
  knowledge base, administration, partner, mobile) are excluded.
- **Alternatives considered**: Two separate SPAs (more setup for little gain in a PoC); a UI
  component library (adds weight; the PoC UI is small); Tailwind for the design tokens (a second
  styling system next to CSS modules for no gain at this size); Google Fonts CDN (third-party
  requests from a local PoC, fails offline).

## R21. Rate limiting

- **Decision**: ASP.NET Core rate-limiting middleware on public endpoints (submission:
  10/hour per IP; claimant access: R10). Gateway-side per-tenant token buckets
  (`System.Threading.RateLimiting`) on model calls so one tenant cannot exhaust the shared provider
  quota; the SDK's built-in retries handle `429`/`5xx` (max 2).

## R22. Local orchestration with Podman and Aspire

- **Decision**: .NET Aspire AppHost orchestrates: PostgreSQL (pgvector image) with databases
  `warranty` and `knowledge`, Azurite, Keycloak, Ollama (embedding model pulled on first run),
  the migration service, the API, and the Vite dev server. Aspire is pointed at Podman with
  `DOTNET_ASPIRE_CONTAINER_RUNTIME=podman`. Secrets (Anthropic API key) are Aspire parameters
  stored in user secrets, never in source.
- **Alternatives considered**: Podman Compose only (works, but loses the Aspire dashboard,
  service discovery and wiring of connection strings).

## R23. Risk level derivation (clarification 2026-10-03, FR-017)

- **Decision**: The risk level is computed deterministically by `IRiskAssessor` from the **union**
  of deterministic signals and the signals listed in the Decision Agent's `risk.signals`; the
  model's own `risk.level` is recorded for display but never used by the guardrails.
  - No signal at all → `Low` (score 0). This is the only way to reach `Low`.
  - One or more signals → at least `Medium`; `High` when the score reaches the tenant's
    `risk_high_threshold` (default 60).
  - Score = `min(100, Σ severity weights)` with weights `Low` 10 · `Medium` 25 · `High` 40. Fixed
    severities for deterministic signals: `MANIPULATION_ATTEMPT`, `EVIDENCE_REUSED`,
    `SERIAL_MISMATCH_PHOTO` → High; `DUPLICATE_SERIAL_CLAIM`, `PRODUCT_NOT_IN_CATALOG`,
    `SOURCE_INCONSISTENCY`, `PURCHASE_DATE_ANOMALY` → Medium. AI-sourced signals
    (`DAMAGE_INCONSISTENT_WITH_DESCRIPTION`, `OTHER`, and AI-reported duplicates of the codes
    above) → Medium unless the same code was also raised deterministically (then the
    deterministic severity applies, counted once). A purchase-date mismatch raises only
    `PURCHASE_DATE_ANOMALY`, never also `SOURCE_INCONSISTENCY`, so one fact is scored once.
  - The guardrail check `RISK_LOW` passes only when the signal list is empty, so FR-026 and
    FR-027 apply the same condition.
  - Photos that do not show the product or the damage are **not** a signal: the Evidence Agent
    reports them as missing information (`PHOTO_OF_DAMAGE` / `PHOTO_OF_SERIAL_LABEL`), which leads
    to `RequestInformation` when nothing else escalates (US5 scenario 2).
- **Rationale**: One rule ("any signal blocks automatic finalization") is easy to test at its
  boundary and removes the earlier FR-026/FR-027 asymmetry. The score now only orders the review
  queue and separates Medium from High; it no longer decides whether a claim is automated.
- **Alternatives considered**: Weighted score with a Medium threshold (lets a single low-severity
  signal through, and the weights become an untested policy decision); a closed "suspicious" subset
  that blocks while other signals only add to the score (two lists to keep consistent across the
  spec, data model and prompts).

## R24. Reviewer loop and the automatic information-request limit (clarification 2026-10-03)

- **Decision**: Two counters on the claim, written only by the `ActionExecutor` and read by the
  guardrails as case facts:
  - `reviewer_info_requested` (bool, sticky): set when a reviewer's `RequestInformation` decision
    is executed; never cleared. Any later round evaluates fully (so the reviewer sees a fresh AI
    recommendation) but the check `NOT_RETURNED_FROM_REVIEW` fails and the disposition is
    `HumanReview` with reason `RETURNED_AFTER_REVIEWER_REQUEST`, whatever the AI recommends
    (FR-028, FR-034).
  - `auto_info_request_count` (int): incremented when a guardrail-issued `RequestInformation`
    action is executed (intake short-circuit or AI `REQUEST_MORE_INFORMATION`). When a round would
    produce `RequestInformation` and the count is already 2, the check
    `AUTO_INFO_REQUESTS_WITHIN_LIMIT` fails and the disposition is `HumanReview` with reason
    `INFO_INCOMPLETE_AFTER_2_REQUESTS` (FR-010). Reviewer requests do not increment it. The limit
    is a constant in `Warranty.Guardrails` (`MaxAutomaticInformationRequests = 2`), not a tenant
    setting, because the spec fixes it.
  - Both checks run in the intake short-circuit as well (they are deterministic and do not need
    the missing information), so FR-028 precedence over FR-010 holds.
- **Rationale**: Storing the facts on the claim keeps the guardrail engine pure (no history
  queries) and makes both rules unit-testable from `CaseFacts` alone.
- **Alternatives considered**: Deriving both from `review_decisions` and run history at evaluation
  time (an extra query inside a "pure" engine); a tenant-configurable limit (not required; can be
  added later behind the same constant).

## R25. Claimant-facing explanations and duplicate detection (clarification 2026-10-03)

- **Decision — explanations**: `claims.final_explanation` is filled from the AI's
  `claimantExplanation` for automatic decisions and from the reviewer's
  `review_decisions.claimant_explanation` for reviewer decisions (FR-036, FR-037). The review API
  requires `claimantExplanation` (20–1,500 characters) for `Approve`/`Reject` and rejects it for
  `RequestInformation`; the SPA pre-fills it from the AI's text only when the reviewer's decision
  matches the AI decision (`APPROVE`↔`Approve`, `REJECT`↔`Reject`). The internal `justification`
  is never copied into any claimant-visible field. A deterministic `ClaimantTextScreen` (in
  `Warranty.Guardrails`) checks claimant-facing text for disclosure terms (risk, fraud, suspicious,
  manipulation, reused, duplicate, signal codes, `EV-`/`POL-`/`GLB-` IDs): on the AI's text it is
  guardrail check `CLAIMANT_TEXT_SAFE` (failure → no automatic finalization → `HumanReview`); on a
  reviewer's text it is a `400` validation error naming the offending term.
- **Decision — duplicates**: `DUPLICATE_SERIAL_CLAIM` is raised when another claim of the same
  tenant has the same normalized serial number and is either not final (`Submitted`,
  `UnderEvaluation`, `PendingInformation`, `UnderReview`) or reached `Approved`/`Rejected` within
  90 days before this claim's claim date (`claims.finalized_at`). Earlier rounds of the same claim
  are the same claim and never count. The window is a constant
  (`DuplicateClaimWindowDays = 90`). The accidental-damage incident count (`maxIncidents`) is a
  separate, all-time count of approved accidental-damage claims for the serial.
- **Rationale**: A separate claimant field keeps fraud reasoning out of claimant views by
  construction; the term screen is a cheap backstop for both AI and human text (closes CHK005).
  An explicit status-and-window rule makes the duplicate signal reproducible in seed data and tests.
- **Alternatives considered**: Reusing the AI explanation when the reviewer agrees (the AI text
  may have been written expecting escalation); showing the justification (leaks internal
  reasoning); any prior claim as a duplicate (escalates every genuine repeat failure).

## R26. Deterministic grounding of exclusion-based rejections (clarification 2026-10-03, FR-027)

- **Decision**: Every `Exclusion` clause carries an `exclusion_code` (`ACCIDENTAL_DAMAGE`,
  `LIQUID_DAMAGE`, `COSMETIC_DAMAGE`, `UNAUTHORIZED_REPAIR`) from its source front matter; seeding
  rejects an exclusion clause whose code is not in its version's `terms.exclusions`. The guardrail
  check `GROUNDED_IN_CLAUSE` passes an AI `REJECT` only when at least one cited `SUPPORTS_REJECTION`
  reference is either (a) a `Period` clause and the deterministic coverage window confirms expiry,
  or (b) an `Exclusion` clause of the applicable version whose `exclusion_code` is in that version's
  `terms.exclusions` **and** at least one photo analysis reports a damage type that maps to that
  code. Fixed mapping (`ExclusionEvidenceMap` in `Warranty.Guardrails`): `CRACKED_SCREEN`,
  `DENTS_OR_IMPACT` → `ACCIDENTAL_DAMAGE`; `LIQUID_INDICATORS`, `CORROSION` → `LIQUID_DAMAGE`;
  `COSMETIC_WEAR` → `COSMETIC_DAMAGE`. `UNAUTHORIZED_REPAIR` has no photo evidence type, so a
  rejection on that ground always goes to a reviewer. Tenant B's accidental-damage allowance is
  coverage, not an exclusion: `AccidentalDamageRule` decides it, and an accidental claim beyond the
  allowance is rejected only through a cited clause that the rule confirms.
- **Rationale**: The AI can neither invent an exclusion the tenant does not have nor apply one
  without evidence; the check reads only structured terms and schema-constrained photo output.
- **Alternatives considered**: Human review for every exclusion (loses the automated tenant
  comparison in US2); free-text matching of clause wording (not deterministic).

## R27. Evidence matching tolerances (clarification 2026-10-03, FR-016)

- **Decision**: One pure class, `EvidenceMatchRules` in `Warranty.Guardrails`, used by the
  `invoice_validation` tool, the serial-in-photo comparison and the guardrail conflict checks:
  - serial and model code: upper-case, remove spaces, `-`, `_`, `.`; exact match
  - purchase date: exact calendar date (the extraction schema already returns ISO dates)
  - price: `|invoice − claim| ≤ max(1% of claim price, 1.00)` in the claim currency
  - seller: lower-case, strip punctuation, collapse spaces, drop trailing legal suffixes (`inc`,
    `incorporated`, `ltd`, `limited`, `llc`, `gmbh`, `ag`, `sa`, `bv`, `plc`, `co`, `corp`,
    `corporation`); exact match
  - a field the invoice does not show (`null` in the extraction) is not a conflict; a missing
    invoice field the claim needs is missing information instead.
- **Rationale**: Identifiers and dates are what fraud alters; price and seller vary honestly.
  Centralizing the rules keeps the tool, the risk signal and the guardrail consistent.
- **Alternatives considered**: Fuzzy string similarity for sellers (non-obvious thresholds, harder
  to explain to reviewers).

## R28. Personal data sent to the AI provider (clarification 2026-10-03, FR-006a)

- **Decision**: R15 becomes a requirement: the context builder never places customer name, email,
  phone or street address in a prompt (placeholders `[CUSTOMER]`, `[EMAIL]`, `[PHONE]`,
  `[ADDRESS]`), and `IPiiRedactor` masks emails and phone numbers in all free text before sending
  and logging. Evidence files are sent as submitted (synthetic in the PoC; image redaction stays a
  documented extension). Every chat provider in `AiGateway` configuration must declare
  `"NoTraining": true`; the gateway refuses to start a route whose provider does not, and the
  Anthropic provider is configured with an API key under commercial terms (API inputs are not used
  for model training by default). A unit test asserts that a rendered prompt for a seeded claim
  contains none of the customer's identifiers.
- **Rationale**: Turns an implementation habit into a checked guarantee and records the provider
  data-use condition explicitly.
- **Alternatives considered**: Sending extracted invoice fields only (loses invoice analysis);
  no restriction (contradicts FR-006a).

## R29. Separation of duties (clarification 2026-10-03, Actors, FR-034)

- **Decision**: Staff users may hold several realm roles. `RecordReviewDecision` refuses a decision
  when `claims.submitted_by` equals the reviewer's `sub`: `403` ProblemDetails "You submitted this
  claim; another reviewer must decide it", a `SELF_REVIEW_REFUSED` security event, no state change.
  The review queue still lists such claims for that user, marked "You submitted this claim", with
  the decision actions disabled. Claimant-channel submissions (`submitted_by = claimant`) never
  match. The Keycloak realm adds `agent-reviewer.aurora` (roles `claims-agent` + `claims-reviewer`)
  to exercise the rule.
- **Rationale**: A single deterministic check closes the self-approval gap while allowing
  realistic multi-role accounts.
- **Alternatives considered**: One role per user (hides the problem rather than enforcing it).

## R30. Security events: attribution and auditor access (clarification 2026-10-03, FR-041a)

- **Decision**: `audit.security_events` stays append-only (INSERT/SELECT grants + trigger) and
  RLS-scoped by `tenant_id`; rows with `tenant_id = NULL` are invisible to the application role
  and readable only by platform operators (owner connection, database or telemetry). Attribution:
  staff denials → the actor's tenant; claimant access failures → the channel's tenant; retrieval
  and tool scope violations → the run's tenant; refused self-review → the tenant; unknown claimant
  channel → `NULL`. To satisfy "never reveal whether a target exists", a staff lookup of a claim or
  evidence ID that is not visible in the actor's tenant writes the **same** tenant-visible event
  (`ACCESS_DENIED`, target = the ID as supplied) whether the ID does not exist or belongs to another
  tenant; when it belongs to another tenant, an additional operator-only event
  (`CROSS_TENANT_ACCESS_DENIED`, `tenant_id = NULL`, both tenants in `details`) is written through
  the `SECURITY DEFINER` function `audit.record_operator_event` (RLS blocks the application role
  from inserting tenant-less rows directly). New
  endpoint `GET /api/security-events` (role `auditor`, paged, newest first) returns `occurredAt`,
  `kind`, `actor` (staff display name, or "claimant channel"), `target` and never `details` or the
  source IP; a staff page "Security events" lists them.
- **Rationale**: Auditors see their own staff's denied attempts and claimant access failures
  without learning anything about other tenants; operators keep the full picture.
- **Alternatives considered**: One tenant-visible cross-tenant event (reveals that the target
  exists elsewhere); operator-only events (contradicts FR-041a).

---

## Resolved unknowns

All Technical Context items in `plan.md` are resolved; no `NEEDS CLARIFICATION` remains.
Items to verify against live documentation during implementation (not blockers): exact Aspire
integration package names for Keycloak, Ollama and Vite hosting; the C# binding for the
`fallbacks: "default"` request field; per-model structured-output support (read from the Models
API at startup, R4); the exact `@fontsource` package names for Fraunces (with its optical-size
axis, if offered), Inter and JetBrains Mono (R20).
