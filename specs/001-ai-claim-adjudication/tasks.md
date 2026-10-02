---

description: "Task list for the AI-powered warranty claim adjudication PoC"
---

# Tasks: AI-Powered Warranty Claim Adjudication (Multi-Tenant PoC)

**Input**: Design documents from `/specs/001-ai-claim-adjudication/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Tests**: Included. The plan requires unit tests, integration tests and deterministic AI evaluation tests,
and the spec's success criteria (SC-003, SC-004, SC-005, SC-010) need automated verification.
Within each story, write the tests first and make sure they fail before implementing.

**Organization**: Tasks are grouped by user story so each story can be implemented and tested as
an increment. Scenario IDs (S1–S15) refer to [quickstart.md §4](./quickstart.md).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on an incomplete task)
- **[Story]**: Which user story the task belongs to (US1–US6)
- Paths follow the plan's layout: .NET projects in `src/Warranty.*`, SPA in `src/web`, tests in
  `tests/`, seed data in `seed/`, Keycloak realm in `infra/keycloak/`

## Fixed identifiers used across tasks

| Item | Value |
|------|-------|
| Aurora tenant | id `11111111-1111-7111-8111-111111111111`, slug `aurora`, channel `aurora.localhost`, namespace `tenant-aurora` |
| Borealis tenant | id `22222222-2222-7222-8222-222222222222`, slug `borealis`, channel `borealis.localhost`, namespace `tenant-borealis` |
| Worker principal | `adjudication-service` (system principal, roles `adjudication-service`) |
| Replay fixtures | `tests/fixtures/ai-recordings/{scenarioId}/{agent}-{callIndex}.json`; scenario selected by the claim's serial number via `seed/golden/scenarios.json` |

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Solution, projects, tooling and identity configuration

- [ ] T001 Create the repository skeleton: `Warranty.slnx` at the repo root and the directories `src/`, `tests/`, `tests/fixtures/ai-recordings/`, `seed/global/`, `seed/tenants/aurora/policies/`, `seed/tenants/borealis/policies/`, `seed/evidence/`, `seed/golden/`, `infra/keycloak/`, `tools/`; extend `.gitignore` with `bin/`, `obj/`, `.vs/`, `*.user`, `node_modules/`, `src/web/dist/`, `artifacts/`
- [ ] T002 Create `Directory.Build.props` (TargetFramework `net10.0`, `Nullable` enable, `ImplicitUsings` enable, `TreatWarningsAsErrors` true, `LangVersion` latest, .NET analyzers on), `Directory.Packages.props` (central package management with versions for: Aspire hosting/client packages, EF Core 10 + Npgsql.EntityFrameworkCore.PostgreSQL, Pgvector + Pgvector.EntityFrameworkCore, Anthropic, Microsoft.Extensions.AI, OllamaSharp, Azure.Storage.Blobs, SkiaSharp, JsonSchema.Net, OpenTelemetry, xunit.v3, Shouldly, NSubstitute, NetArchTest.Rules, Testcontainers.PostgreSql, Testcontainers.Azurite, Microsoft.AspNetCore.Mvc.Testing) and `.editorconfig` at the repo root
- [ ] T003 Create class library projects and add them to `Warranty.slnx`: `src/Warranty.Domain`, `src/Warranty.Application` (→ Domain, Guardrails), `src/Warranty.Guardrails` (→ Domain only), `src/Warranty.AI.Harness` (→ Application, Domain, Guardrails), `src/Warranty.AI.Gateway` (→ Application; packages Anthropic, Microsoft.Extensions.AI, OllamaSharp, SkiaSharp), `src/Warranty.Knowledge` (→ Application, Domain), `src/Warranty.Infrastructure` (→ Application, Domain; EF Core, Npgsql, Pgvector, Azure.Storage.Blobs), `src/Warranty.Integrations.Simulated` (→ Application, Domain)
- [ ] T004 Create host projects and add them to `Warranty.slnx`: `src/Warranty.Api` (ASP.NET Core empty web, Minimal APIs, references all `src/` libraries), `src/Warranty.MigrationService` (worker service → Infrastructure, Knowledge, Integrations.Simulated, AI.Gateway), `src/Warranty.AppHost` (Aspire AppHost), `src/Warranty.ServiceDefaults` (Aspire service defaults, referenced by Api and MigrationService)
- [ ] T005 Create test and tool projects and add them to `Warranty.slnx`: `tests/Warranty.UnitTests` (xunit.v3, Shouldly, NSubstitute, NetArchTest → all `src/` libraries), `tests/Warranty.IntegrationTests` (xunit.v3, Shouldly, Testcontainers, Mvc.Testing → Warranty.Api), `tests/Warranty.Evaluation` (console → Api libraries; never referenced by `src/`), `tools/Warranty.EvidenceGenerator` (console, SkiaSharp)
- [ ] T006 [P] Scaffold the SPA in `src/web` with Vite + React 19 + TypeScript (strict): add React Router, TanStack Query, oidc-client-ts, react-oidc-context, openapi-fetch, openapi-typescript (dev), Vitest, @testing-library/react, MSW, ESLint; configure `src/web/vite.config.ts` with `server.allowedHosts` for `aurora.localhost` and `borealis.localhost` and an `/api` proxy to the API with `changeOrigin: false` so the original Host header reaches the API; add npm scripts `dev`, `build`, `test`, `lint`, `gen:api`
- [ ] T007 [P] Create the Keycloak realm in `infra/keycloak/warranty-realm.json`: realm `warranty`; public client `warranty-web` (Authorization Code + PKCE, redirect URIs `http://localhost:5173/*`, `http://*.localhost:5173/*`); audience mapper for the API; realm roles `claims-agent`, `claims-reviewer`, `auditor`; user attribute `tenant_id` with a protocol mapper adding claim `tenant_id` to access tokens; synthetic users `agent.aurora`, `reviewer.aurora`, `auditor.aurora` (tenant `11111111-…`) and `agent.borealis`, `reviewer.borealis`, `auditor.borealis` (tenant `22222222-…`) with development-only passwords and one role each

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Domain model, tenancy, persistence with RLS, storage, jobs, audit, AI Gateway, knowledge store, harness framework, seeding and local orchestration — required by every user story

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Domain and ports

- [ ] T008 [P] Create shared enums and value types in `src/Warranty.Domain/Common/` and `src/Warranty.Domain/Claims/`: `Region` (NA, EU), `ClaimStatus`, `ClaimChannel`, `EvidenceKind`, `Disposition`, `AiDecision`, `CoverageDetermination`, `RiskLevel`, `RiskSignalCode`, `RiskSignalSource`, `FinalOutcome`, `DecidedBy`, `ReviewDecisionKind`, `TrailStep`, `SecurityEventKind` — values exactly as in data-model.md
- [ ] T009 [P] Create tenancy, catalog, CRM and policy entities in `src/Warranty.Domain/Tenancy/` (`Tenant`, `TenantChannel`, `TenantSettings`), `src/Warranty.Domain/Catalog/` (`Product` with `ClaimValue`, `ProductSerial`), `src/Warranty.Domain/Crm/Customer.cs`, `src/Warranty.Domain/Policies/` (`WarrantyPolicy`, `PolicyVersion`, `PolicyClause`, `CoverageTerms` record matching the `terms` JSON in data-model.md)
- [ ] T010 [P] Create the claim aggregate in `src/Warranty.Domain/Claims/Claim.cs`, `ClaimEvidence.cs`, `ClaimJob.cs`: fields per data-model.md; state transitions enforced by methods (`StartEvaluation`, `RequestInformation(items)`, `EscalateToReview`, `FinalizeApproved(explanation, decidedBy)`, `FinalizeRejected(explanation, decidedBy)`, `AddSupplement()` incrementing `CurrentRound`); invalid transitions throw `InvalidClaimTransitionException`; `Approved`/`Rejected` terminal
- [ ] T011 [P] Create adjudication, review, audit, AI-ops and integration entities in `src/Warranty.Domain/Adjudication/` (`AdjudicationRun`, `IntakeResult`, `EvidenceFinding`, `RetrievedPolicyRef`, `PolicyAssessment`, `RiskAssessment` (with `Stage` = `Intake` | `Full`), `RiskSignal`, `Recommendation`, `GuardrailEvaluation`, `GuardrailCheck`), `src/Warranty.Domain/Review/ReviewDecision.cs`, `src/Warranty.Domain/Audit/` (`DecisionTrailEntry`, `SecurityEvent`), `src/Warranty.Domain/AiOps/` (`ModelCall`, `ToolCall`, `RagQuery`), `src/Warranty.Domain/Integration/` (`ServiceCenter`, `RepairRequest`, `Notification`) per data-model.md; `Recommendation` has no setters after construction
- [ ] T012 Declare Application ports in `src/Warranty.Application/Abstractions/`: `ITenantContext` (TenantId, TenantSlug, KnowledgeNamespace, PrincipalId, PrincipalName, Roles, CorrelationId, IsSystem) and `TenantContextScope` (AsyncLocal, `Begin(...)` for workers); `Storage/IDocumentStore.cs`; `Knowledge/IKnowledgeRetriever.cs`, `IKnowledgeIngestor.cs`, `ICaseKnowledgeProvider.cs` with the records from contracts/rag.md; `AI/IAiGateway.cs` with every type from contracts/ai-gateway.md (`AiCallContext`, `AiTurnRequest`, `AiTurnResult`, `AiConversation` append-only, content parts incl. `ProviderOpaquePart`, `AiUsage`, `AiFailure`, `AiEmbeddingRequest/Result`, `PromptRef`, `AiToolDefinition`, `AiOutputSchema`); `Integrations/` (`ICrmClient`, `IErpSerialRegistry`, `IServiceNetwork`, `IRepairRequestService`, `INotificationService`); `Jobs/IJobQueue.cs`; `Audit/IDecisionTrailWriter.cs`, `ISecurityEventWriter.cs`; `Persistence/` repository interfaces (`IClaimRepository`, `ITenantRepository`, `ICatalogRepository`, `IPolicyRepository`, `IAdjudicationRepository`, `IReviewRepository`, `IAiOpsRepository`, `IUnitOfWork`); `Adjudication/IAdjudicationRunner.cs` (`RunAsync(claimId, round, ct)`). No method takes a tenant ID where the tenant context applies

### Persistence, isolation and infrastructure

- [ ] T013 Implement `src/Warranty.Infrastructure/Persistence/WarrantyDbContext.cs` and `Persistence/Configurations/*.cs`: one schema per domain (`tenancy`, `catalog`, `crm`, `policy`, `claims`, `adjudication`, `review`, `audit`, `aiops`, `integration`), composite FKs including `tenant_id`, `jsonb` columns, `xid` row version on claims, global query filters `e => e.TenantId == tenantContext.TenantId` on every tenant-owned entity
- [ ] T014 [P] Implement `src/Warranty.Infrastructure/Persistence/TenantSessionInterceptor.cs` (DbConnectionInterceptor) that runs `select set_config('app.tenant_id', @tenant, false), set_config('app.kb_namespace', @ns, false)` on every connection open from `ITenantContext`, and resets both on close; throws if there is no tenant context, except on connections tagged `NoTenantScope`, which only the migration service and `PostgresJobQueue.TryDequeueAsync` may open
- [ ] T015 Create the initial EF Core migration for `WarrantyDbContext` in `src/Warranty.Infrastructure/Persistence/Migrations/` and the SQL script `src/Warranty.Infrastructure/Persistence/Sql/001_roles_and_rls.sql`: role `warranty_app` (non-owner, LOGIN) with grants; `ENABLE` + `FORCE ROW LEVEL SECURITY` and policy `tenant_id = current_setting('app.tenant_id')::uuid` on every tenant-owned table; `audit.*` tables grant `INSERT, SELECT` only plus a trigger raising an exception on `UPDATE`/`DELETE`
- [ ] T016 [P] Implement the knowledge database in `src/Warranty.Infrastructure/Persistence/Knowledge/KnowledgeDbContext.cs` and `Persistence/Sql/002_knowledge.sql`: `create extension vector`; `knowledge_documents`; `knowledge_chunks` `PARTITION BY LIST (namespace)` with `embedding vector(768)` and the denormalized filter columns from data-model.md; function `knowledge.ensure_namespace(ns text)` creating a partition plus HNSW cosine index; RLS `namespace = 'global' OR namespace = current_setting('app.kb_namespace')`; role grants for `warranty_app`
- [ ] T017 Implement repositories in `src/Warranty.Infrastructure/Persistence/Repositories/` for every interface declared in T012 (`ClaimRepository`, `TenantRepository`, `CatalogRepository`, `PolicyRepository`, `AdjudicationRepository`, `ReviewRepository`, `AiOpsRepository`, `UnitOfWork`), and `src/Warranty.Infrastructure/DependencyInjection.cs` registering the DbContexts (connecting as `warranty_app`) with the interceptor
- [ ] T018 [P] Implement `src/Warranty.Infrastructure/Storage/BlobDocumentStore.cs`: container `tenant-{slug}` from `ITenantContext`, path `claims/{claimId}/{round}/{evidenceId}{ext}`, SHA-256 computed while uploading, `OpenReadAsync` that refuses blobs outside the current tenant's container; container `knowledge-sources` for seed documents
- [ ] T019 [P] Implement `src/Warranty.Infrastructure/Jobs/PostgresJobQueue.cs`: `EnqueueAsync` inside the caller's transaction; `TryDequeueAsync` using `SELECT … FOR UPDATE SKIP LOCKED` on `claims.claim_jobs` (status `Queued`, `available_at <= now()`), sets `locked_until`, increments `attempts`; `CompleteAsync`/`FailAsync` (max 3 attempts, exponential `available_at`). `TryDequeueAsync` calls the `SECURITY DEFINER` function `claims.dequeue_claim_job(worker_id, lock_seconds)` (created in `src/Warranty.Infrastructure/Persistence/Sql/004_job_queue.sql`; `warranty_app` has EXECUTE only) on a `NoTenantScope` connection; it returns only job id, tenant id, claim id, round and correlation id. `CompleteAsync` and `FailAsync` run inside the job's tenant context
- [ ] T020 [P] Implement `src/Warranty.Infrastructure/Audit/DecisionTrailWriter.cs` (per-claim gap-free `seq`, canonical JSON serialization, `hash = SHA-256(prev_hash ‖ canonical_json)`), `Audit/HashChainVerifier.cs` (recomputes and reports first broken `seq`), and `Audit/SecurityEventWriter.cs` (append-only, nullable tenant)
- [ ] T021 [P] Implement simulated integrations in `src/Warranty.Integrations.Simulated/`: `SimulatedCrmClient.cs` (find-or-create customer by tenant + normalized email in `crm.customers`), `SimulatedErpSerialRegistry.cs` (lookup in `catalog.product_serials`), `SimulatedServiceNetwork.cs` (first service center matching region), `SimulatedRepairRequestService.cs` and `SimulatedNotificationService.cs` (insert rows in `integration.*`, nothing is sent), `PaymentStub.cs` (no-op), `DependencyInjection.cs`

### API host, tenancy and security

- [ ] T022 Implement tenant resolution in `src/Warranty.Api/Tenancy/TenantResolutionMiddleware.cs` and `HttpTenantContext.cs`: for authenticated staff use the `tenant_id` token claim; for `/api/public/*` map the request Host (lower-case, port stripped) through a cached `tenant_channels` lookup; unknown channel → 404 ProblemDetails + `UNKNOWN_CHANNEL` security event; never read tenant values from body, query or route; populate the scoped `ITenantContext` before endpoint execution
- [ ] T023 [P] Implement authentication and authorization in `src/Warranty.Api/Auth/`: JWT bearer validation against the Keycloak realm (authority and audience from Aspire configuration), mapping realm roles to role claims; policies `ClaimsAgent`, `ClaimsReviewer`, `Auditor`, `ReviewerOrAuditor`, `AnyStaff`; `ClaimantTokenService.cs` issuing and validating API-signed tokens (scheme `Claimant`, 30-minute lifetime, claims `tenant_id`, `claim_id`, `scope=claimant`) with the signing key from configuration/user secrets
- [ ] T024 Compose the API in `src/Warranty.Api/Program.cs`: `AddServiceDefaults`, ProblemDetails (RFC 9457) with `correlationId` extension, middleware writing `X-Correlation-Id` from `Activity.Current.TraceId`, OpenAPI document, authentication/authorization, tenant resolution middleware, rate limiter registration, endpoint-group registration placeholders (`MapPublicEndpoints`, `MapClaimEndpoints`, `MapReviewEndpoints`, `MapTraceEndpoints`, `MapReferenceEndpoints`), and DI for Infrastructure, Integrations, Gateway, Knowledge, Harness, Guardrails and Application
- [ ] T025 Implement `src/Warranty.Api/Workers/ClaimJobWorker.cs` (BackgroundService): poll `IJobQueue`, for each job open a DI scope and `TenantContextScope.Begin(job.TenantId, principal: "adjudication-service", correlationId: job.CorrelationId)`, call `IAdjudicationRunner.RunAsync(job.ClaimId, job.Round)`, complete or fail the job; infrastructure exceptions retry, never retried for AI outcomes

### AI Gateway

- [ ] T026 Implement the gateway core in `src/Warranty.AI.Gateway/`: `AiGateway.cs` (implements `IAiGateway`), `Routing/RouteOptions.cs` + `Routing/ModelProfileRegistry.cs` (routes and profiles per contracts/ai-gateway.md), `Prompts/PromptTemplateRegistry.cs` (loads `PromptTemplates/*.vN.md` embedded resources with front matter `id`, `version`, `route`, `outputSchema`), `RateLimiting/TenantRateLimiter.cs` (token bucket per tenant from `RateLimits:PerTenantRequestsPerMinute`), `Usage/UsageRecorder.cs` (writes `aiops.model_calls` with cost from `Pricing`), and an `ActivitySource("Warranty.AI.Gateway")` span per call with `gen_ai.*` and `warranty.*` attributes
- [ ] T027 [P] Implement `src/Warranty.AI.Gateway/Redaction/RegexPiiRedactor.cs` (`IPiiRedactor`): replace email addresses with `[EMAIL]` and phone numbers with `[PHONE]` in text and untrusted-text parts before sending and before logging
- [ ] T028 Implement `src/Warranty.AI.Gateway/Providers/Anthropic/AnthropicModelProvider.cs` with the official `Anthropic` SDK only: map `AiConversation` to beta `MessageCreateParams` (`client.Beta.Messages.Create`); system prompt from the template with a `CacheControlEphemeral` breakpoint after platform content; `UntrustedTextPart` rendered as `<untrusted_claim_content label="…">…</untrusted_claim_content>`; images as base64 image blocks; PDFs as document blocks with `Base64PdfSource`; tools with `strict: true` and `tool_choice: auto` (never forced); `OutputConfig.Format = JsonOutputFormat` when the route's model profile supports structured outputs, otherwise schema-in-prompt + strict parse; `OutputConfig.Effort` from the route only when the profile supports effort (never for `claude-haiku-4-5`); adaptive thinking (never disabled) on `claude-opus-5-5`; server-side refusal fallback `fallbacks: "default"` with beta `server-side-fallback-2026-07-01`; thinking blocks returned as `ProviderOpaquePart` and echoed back unchanged; map `stop_reason` (`refusal` → `Refused`, `max_tokens` → `Truncated`, `tool_use` → `ToolCalls`) and `Usage` (input, output, cache read/write tokens); catch SDK exceptions most-specific-first into `AiFailure`; downscale every `ImagePart` with `src/Warranty.AI.Gateway/Imaging/ImageDownscaler.cs` (SkiaSharp, long edge ≤ 1,500 px, JPEG re-encode) before sending
- [ ] T029 [P] Implement `src/Warranty.AI.Gateway/Providers/Embeddings/OllamaEmbeddingProvider.cs`: `IEmbeddingGenerator<string, Embedding<float>>` via OllamaSharp for model `nomic-embed-text` (768 dimensions), endpoint from Aspire configuration; batch embedding; dimension check
- [ ] T030 [P] Implement deterministic providers in `src/Warranty.AI.Gateway/Providers/Replay/`: `ReplayModelProvider.cs` (reads `tests/fixtures/ai-recordings/{scenarioId}/{agent}-{callIndex}.json`, each file = serialized `AiTurnResult` incl. `stop`, `toolCalls`, `structuredOutput`, `usage`, optional `failure`; scenario chosen by `IReplayScenarioSelector` mapping the claim serial number to a scenario via `seed/golden/scenarios.json`; optional `--record` mode wrapping the Anthropic provider to write fixtures), `ScriptedModelProvider.cs` (in-memory queue for unit tests), `HashEmbeddingGenerator.cs` (deterministic 768-dim vectors from token hashes for tests)
- [ ] T031 Register the gateway in `src/Warranty.AI.Gateway/DependencyInjection.cs` (provider selection `AiGateway:Mode` = `live` | `replay`; model capability load from the Models API at startup with config fallback) and add the `AiGateway` configuration section from contracts/ai-gateway.md to `src/Warranty.Api/appsettings.json`

### Tenant-aware RAG

- [ ] T032 Implement knowledge ingestion in `src/Warranty.Knowledge/Ingestion/`: `FrontMatterParser.cs` (YAML front matter per contracts/rag.md), `KnowledgeSourceValidator.cs` (namespace must be `global` or `tenant-{slug}` of the tenant being ingested; versions of one policy must not overlap), `ClauseChunker.cs` (split at `##` headings; clause key = heading prefix such as `AUR-WP-3.2`; long clauses split at paragraphs with the key repeated), `KnowledgeIngestor.cs` (upserts `policy.*` rows incl. structured `terms`, embeds via the `embedding` route, writes chunks into the namespace partition after `knowledge.ensure_namespace`, skips unchanged checksum)
- [ ] T033 Implement retrieval in `src/Warranty.Knowledge/Retrieval/KnowledgeRetriever.cs` and `NamespaceGuard.cs` per contracts/rag.md: namespace and roles from `ITenantContext` only; SQL hard filters (document type, category/model or NULL, region or empty, `effective_from ≤ purchaseDate ≤ coalesce(effective_to, ∞)`, classification, `allowed_roles && principal roles`) before cosine ranking; distinct-version check → `NoApplicablePolicy` / `AmbiguousPolicyVersion`; always include `Period` and `Exclusion` clauses of the selected version; post-retrieval assertion (namespace and tenant) failing closed with a `RETRIEVAL_SCOPE_VIOLATION` security event; one `aiops.rag_queries` row per call

### AI Harness framework

- [ ] T034 Implement the harness core in `src/Warranty.AI.Harness/Execution/` and `Context/`: `AdjudicationContext.cs` (per contracts/agents-and-tools.md), `ReferenceRegistry.cs` (issues `EV-n`, `POL-n`, `GLB-n`, resolves them, rejects unknown IDs, serializes to `adjudication_runs.reference_map`), `AgentContracts.cs` (`IAgent<TIn,TOut>`, `AgentDescriptor`, `AgentResult<T>`, `AgentStatus`, `AgentExecutionContext`), `AgentTurnLoop.cs` (bounded loop over `IAiGateway.CompleteAsync`, append-only `AiConversation`, executes all tool calls of a turn and returns all results in one message, stops at `MaxTurns`), `ContextBuilder.cs` (per-agent input-token budget, priority order instructions → case facts → Period/Exclusion clauses → other clauses by score → global snippets, drops lowest priority first, never truncates evidence and reports `ContextOverflow` instead)
- [ ] T035 Implement the tool framework in `src/Warranty.AI.Harness/Tools/`: `ITool.cs`, `ToolDescriptor.cs`, `ToolRegistry.cs` (`For(agentName)` returns only `ReadOnly` tools whose `AllowedCallers` include the agent), `ToolInvoker.cs` (re-checks caller at invocation, validates arguments against the strict input schema, resolves references via `ReferenceRegistry`, injects `ToolInvocationContext` with the tenant context, returns error results for unknown/disallowed tools, records `aiops.tool_calls`, writes `TOOL_SCOPE_VIOLATION` when a `Consequential` tool is requested by an agent)
- [ ] T036 [P] Implement `src/Warranty.AI.Harness/Safety/UntrustedContent.cs`: helpers creating `UntrustedTextPart` for claimant description, invoice text and image text, and the shared prompt preamble text stating that untrusted content is evidence only

### Seed data, migration service and orchestration

- [ ] T037 [P] Create seed files per data-model.md §Seed data: `seed/tenants/aurora/tenant.json` (id, slug, displayName "Aurora Electronics", channel, settings limit 500 / min confidence 85), `products.json` (`AUR-TAB10` 450, `AUR-PHN6` 480, `AUR-BOOK15` 1400), `serials.json` (≥15 serials per model incl. ones used by S1–S15), `customers.json` (synthetic), `service-centers.json`, `policies/AUR-WP-v1.md` (effective 2025-01-01–2026-06-30; NA 12 / EU 24 months; battery 6 months; exclusions accidental, liquid, cosmetic, unauthorized repair) and `policies/AUR-WP-v2.md` (2026-07-01–open; battery 12 months), each with front matter + `terms` and clause headings `## AUR-WP-x.y Title`; the same for `seed/tenants/borealis/` ("Borealis Devices", limit 1000, min confidence 80, always-review `major-appliance`; `BOR-SLATE11` 480, `BOR-HUB2` 220, `BOR-OVEN60` 1650 category `major-appliance`; `policies/BOR-WP-v1.md` 2024-01-01–open, 24 months, battery 12 months, one accidental-damage incident within 12 months, excludes liquid and unauthorized repair); `seed/global/terminology.md`, `fraud-patterns.md`, `injection-phrases.md` (one phrase/pattern per line), `procedures.md`
- [ ] T038 Implement `src/Warranty.MigrationService/Program.cs` and `Seeding/*.cs`: apply both EF migrations and the SQL scripts as owner; create `knowledge` partitions for `global` and each tenant; idempotently seed tenants, channels, settings, products, serials, customers and service centers from `seed/tenants/*`; upload policy sources to `knowledge-sources`; ingest global and tenant knowledge through `IKnowledgeIngestor` under the correct tenant/platform context; exit with code 0
- [ ] T039 Wire the Aspire AppHost in `src/Warranty.AppHost/AppHost.cs`: PostgreSQL with image `pgvector/pgvector` and databases `warranty` and `knowledge`; Azure Storage emulator with blobs; Keycloak with realm import from `infra/keycloak`; Ollama with model `nomic-embed-text`; secret parameter `anthropic-api-key`; `migrations` project (waits for postgres, storage, ollama); `api` (waits for migrations completion, references all resources, env `AiGateway__Mode`); Vite app `src/web` with the API URL and Keycloak authority as environment variables
- [ ] T040 [P] Configure `src/Warranty.ServiceDefaults/Extensions.cs`: OpenTelemetry tracing/metrics/logging with OTLP exporter, sources `Warranty.AI.Harness`, `Warranty.AI.Gateway`, `Warranty.Knowledge`, `Warranty.Tools`, `Warranty.Api`; health checks `/health` and `/alive`; HTTP resilience defaults; service discovery

### Web foundation

- [ ] T041 [P] Build the SPA shell in `src/web/src/app/`: `main.tsx`, `App.tsx`, `routes.tsx` (claimant area when the host is a tenant channel, staff area under `/staff`), `AuthProvider.tsx` (react-oidc-context with Keycloak authority/client from env, PKCE), `QueryProvider.tsx`, `Layout.tsx`, `RequireRole.tsx`
- [ ] T042 [P] Generate the typed API client: `gen:api` script running openapi-typescript on `specs/001-ai-claim-adjudication/contracts/rest-api.openapi.yaml` into `src/web/src/shared/api/schema.d.ts`, and `src/web/src/shared/api/client.ts` (openapi-fetch client with middleware adding the OIDC bearer token for staff calls or the in-memory claimant token for claimant calls, and surfacing ProblemDetails)

### Foundational tests

- [ ] T043 [P] Write architecture tests in `tests/Warranty.UnitTests/Architecture/DependencyRulesTests.cs` (NetArchTest): Domain and Guardrails reference no AI, Harness, Gateway, Knowledge or Infrastructure assembly; only `Warranty.AI.Gateway` references `Anthropic`; `Warranty.AI.Harness` does not reference `Warranty.AI.Gateway`; Application does not reference EF Core or Npgsql; `Warranty.Integrations.Simulated` is referenced only by Api and MigrationService; `ApprovedAction` has no public constructor
- [ ] T044 [P] Write claim state machine unit tests in `tests/Warranty.UnitTests/Domain/ClaimStateMachineTests.cs` covering every allowed transition in data-model.md and rejection of transitions out of `Approved`/`Rejected` and supplements on terminal claims
- [ ] T045 Build the integration test fixture in `tests/Warranty.IntegrationTests/Infrastructure/WarrantyAppFixture.cs`: Testcontainers PostgreSQL (`pgvector/pgvector`) and Azurite; run the migration service seeding against them; `WebApplicationFactory<Program>` with `AiGateway:Mode=replay`, `HashEmbeddingGenerator`, a test authentication handler issuing staff principals for the six seeded users, helpers for claimant tokens and `Host` headers; helper `WaitForClaimStatusAsync`
- [ ] T046 Write RLS tests in `tests/Warranty.IntegrationTests/Isolation/RowLevelSecurityTests.cs`: connected as `warranty_app` with `app.tenant_id` = Aurora, raw SQL against every tenant-owned table returns no Borealis rows; inserting a row with Borealis `tenant_id` fails; `UPDATE`/`DELETE` on `audit.decision_trail_entries` fail; knowledge chunks of `tenant-borealis` are invisible with `app.kb_namespace = tenant-aurora`; `warranty_app` cannot select another tenant's `claims.claim_jobs` rows directly, and `claims.dequeue_claim_job` returns only the five job header columns

**Checkpoint**: Foundation ready — `aspire run` starts all resources, migrations seed both tenants and index knowledge, architecture and RLS tests pass

---

## Phase 3: User Story 1 - Automated adjudication of a clear-cut claim (Priority: P1) 🎯 MVP

**Goal**: A complete, consistent, low-risk claim is submitted, analyzed by the Intake, Evidence, Policy and Decision agents, passes the guardrails and is finalized automatically (approve or reject) with an explanation citing evidence and versioned policy clauses; every step is in the decision trail.

**Independent Test**: Scenarios S1 (auto-approved, cites `AUR-WP` coverage clause + version) and S2 (auto-rejected, coverage window confirmed) via the scenario tests or the claimant portal; the trace API shows all steps.

### Tests for User Story 1 ⚠️ write first, ensure they fail

- [ ] T047 [P] [US1] Write guardrail disposition unit tests in `tests/Warranty.UnitTests/Guardrails/DispositionRulesTests.cs`: `AutoApprove` only when every FR-026 condition holds (flip each condition → not `AutoApprove`), `AutoReject` only when every FR-027 condition holds, boundaries claim value = limit (allowed) and confidence = minimum (allowed), `auto_approve_enabled`/`auto_reject_enabled` false → `HumanReview`
- [ ] T048 [P] [US1] Write coverage window unit tests in `tests/Warranty.UnitTests/Guardrails/CoverageWindowTests.cs`: standard months by region, component months (battery), month-end purchase dates, claim on the last covered day, `NoApplicablePolicy` input
- [ ] T049 [P] [US1] Write unit tests in `tests/Warranty.UnitTests/Harness/ReferenceRegistryTests.cs` and `tests/Warranty.UnitTests/Harness/SchemaValidatorTests.cs`: issued/unknown `EV-n`/`POL-n`, `GLB-n` not accepted as policy refs, schema violations, confidence outside 0–100, over-length text, empty `evidenceRefs`, APPROVE/REJECT without `policyRefs`
- [ ] T050 [P] [US1] Implement the synthetic evidence generator in `tools/Warranty.EvidenceGenerator/Program.cs` (SkiaSharp: product photos drawing a device outline, optional damage overlays such as crack lines/scorch marks, a serial label text; invoice PDFs with seller, invoice number, date, model code, serial, amount; options to vary rendering so file hashes differ) and generate S1/S2 evidence into `seed/evidence/S1/` and `seed/evidence/S2/`
- [ ] T051 [P] [US1] Create `seed/golden/scenarios.json` with entries S1 and S2 (scenarioId, tenant, serial, claim data, purchase date offset in months, evidence files, expected disposition, expected cited clause keys) and hand-authored replay fixtures conforming to `contracts/schemas/*.schema.json` in `tests/fixtures/ai-recordings/S1/` and `tests/fixtures/ai-recordings/S2/` (intake, evidence-invoice, evidence-photo-n, policy incl. one `warranty_lookup` tool call, decision)
- [ ] T052 [US1] Write scenario integration tests in `tests/Warranty.IntegrationTests/Scenarios/US1_AutomatedAdjudicationTests.cs`: S1 via `POST /api/public/claims` with Host `aurora.localhost` → status `Approved`, `final_decided_by=System`, explanation present, recommendation cites a `POL-n` mapped to an `AUR-WP` clause with version and effective dates, simulated repair request and notification rows exist; S2 → `Rejected` with `COVERAGE_WINDOW_AGREES` passed; claimant access with reference + email returns the outcome without risk data; `GET /api/claims/{id}/trace` as `auditor.aurora` lists ClaimSubmitted … GuardrailsEvaluated → AutoApproved in order; claims-agent submission via `POST /api/claims` reaches the same result

### Implementation for User Story 1

- [ ] T053 [P] [US1] Implement `src/Warranty.Guardrails/Rules/CoverageWindowCalculator.cs`: from `CoverageTerms`, region, component, purchase date and claim date compute `coverageEndDate`, `withinStandardCoverage`, `withinComponentCoverage` (no AI input)
- [ ] T054 [P] [US1] Add the five JSON schemas from `specs/001-ai-claim-adjudication/contracts/schemas/` as embedded resources in `src/Warranty.AI.Harness/Schemas/` and implement `src/Warranty.AI.Harness/Schemas/SchemaValidator.cs` (JsonSchema.Net) plus the range/length/format rules stated in the schema descriptions
- [ ] T055 [P] [US1] Write prompt templates in `src/Warranty.AI.Gateway/PromptTemplates/`: `intake.v1.md`, `evidence-invoice.v1.md`, `evidence-photo.v1.md`, `policy.v1.md`, `decision.v1.md` — platform-owned text only (no tenant data), the untrusted-content rule, instructions to cite only issued `EV-n`/`POL-n` IDs, to use `GLB-n` only as context, to keep claimant explanations free of risk/fraud information, and front matter with id, version, route and output schema id
- [ ] T056 [US1] Implement `src/Warranty.Application/Claims/SubmitClaim.cs`: validate `ClaimSubmissionData` (contracts/rest-api.openapi.yaml) and files (≥1 invoice, 1–8 photos, JPEG/PNG/WebP/PDF by magic bytes, ≤15 MB each; HEIC rejected with 415 and a message asking for JPEG or PNG), find-or-create customer via `ICrmClient`, resolve product/serial via catalog (null when not found), derive region from purchase country else customer country, generate a random 10-char Crockford base32 reference, store evidence via `IDocumentStore` (round 1), persist claim + job in one transaction, write trail entries `ClaimSubmitted`, `TenantResolved`, `EvidenceStored`
- [ ] T057 [US1] Implement submission endpoints: `POST /api/public/claims` in `src/Warranty.Api/Endpoints/Public/PublicClaimEndpoints.cs` (anonymous, tenant from Host) and `POST /api/claims` in `src/Warranty.Api/Endpoints/Claims/ClaimEndpoints.cs` (policy `ClaimsAgent`), both multipart (`claim` JSON part + `invoice` + `photos`), returning `202 SubmissionAccepted`
- [ ] T058 [US1] Implement claimant access and status in `src/Warranty.Application/Claims/ClaimantAccess.cs` and `src/Warranty.Api/Endpoints/Public/ClaimantEndpoints.cs`: `POST /api/public/claims/access` (normalize email lower-case / phone digits, constant-time comparison with the claim's contact, identical 401 for unknown reference or mismatch, `CLAIMANT_ACCESS_FAILED` security event, issue claimant token), `GET /api/public/claims/{reference}` (claimant token must match tenant and claim; returns `ClaimantClaimView` with status, product name, `final_explanation`, requested items; never risk signals), `GET /api/public/tenant`
- [ ] T059 [US1] Implement `src/Warranty.Application/Adjudication/CaseKnowledgeProvider.cs` (`ICaseKnowledgeProvider`): loads claim, product, customer (redacted view: region and country only, `[CUSTOMER]` placeholder), evidence descriptors for the round, and claim-history counts for the serial within the tenant
- [ ] T060 [P] [US1] Implement read-only tools in `src/Warranty.AI.Harness/Tools/Implementations/`: `CustomerLookupTool.cs`, `ProductLookupTool.cs`, `WarrantyLookupTool.cs` (applicable version + structured terms + `CoverageWindowCalculator` results for the claim's region/dates), `InvoiceValidationTool.cs` (field-by-field match of extracted invoice vs claim: date exact, model code and serial normalized exact, amount within 1%, seller case-insensitive), `ClaimHistoryLookupTool.cs` (counts only: prior claims for serial, prior approved accidental claims, evidence SHA-256 reuse matches — same tenant), `SearchPolicyKnowledgeTool.cs`, `SearchGlobalKnowledgeTool.cs`, each with a strict input schema without tenant fields and the allowed callers from contracts/agents-and-tools.md
- [ ] T061 [US1] Implement `src/Warranty.AI.Harness/Agents/IntakeAgent.cs`: deterministic FR-009 checks producing the validation list (`REQUIRED_FIELDS`, `PHOTO_PRESENT`, `INVOICE_PRESENT`, `FILE_TYPES`, `PURCHASE_DATE_NOT_FUTURE`, `PURCHASE_DATE_BEFORE_CLAIM`, `REGION_DETERMINED`) and missing items; `extraction` route call with the description as untrusted content and `intake-extraction` schema; tools `customer_lookup`, `product_lookup`; persists `adjudication.intake_results`
- [ ] T062 [P] [US1] Implement `src/Warranty.AI.Harness/Agents/EvidenceAgent.cs` (passes original image bytes as `ImagePart`; never resizes — the gateway downscales, T028): invoice extraction on `extraction` route (PDF document part or image part), `INVOICE_LEGIBLE` validation from `legible`, photo analysis on `vision` route one call per photo in parallel, `invoice_validation` tool, serial-in-photo comparison, persists `adjudication.evidence_findings` with consistency results and each photo's `confidence`
- [ ] T063 [P] [US1] Implement `src/Warranty.AI.Harness/Agents/PolicyAgent.cs`: build the retrieval query from the intake extraction (not raw claimant text), call `RetrievePolicyClausesAsync` with category/model/region/purchase date, issue `POL-n` references, run the `policy-reasoning` route with tools `warranty_lookup`, `search_policy_knowledge`, `search_global_knowledge` and the `policy-assessment` schema, persist `adjudication.policy_assessments` (version outcome and assessment incl. `confidence`) and `adjudication.retrieved_policy_refs` with document title, version and effective dates
- [ ] T064 [P] [US1] Implement `src/Warranty.AI.Harness/Agents/Risk/RiskAssessor.cs` (`IRiskAssessor`): deterministic signals `PRODUCT_NOT_IN_CATALOG`, `DUPLICATE_SERIAL_CLAIM` (prior claim for the serial in the last 12 months not finalized as Rejected), `EVIDENCE_REUSED` (SHA-256 match in another claim of the tenant), `PURCHASE_DATE_ANOMALY` (invoice date ≠ stated date, or future), `SOURCE_INCONSISTENCY` (any failed invoice/photo consistency check), `SERIAL_MISMATCH_PHOTO`; weights 40/30/40/25/25/40 summed and capped at 100; level by tenant thresholds (≥ `risk_high_threshold` High, ≥ `risk_medium_threshold` Medium); `EVIDENCE_REUSED`, `SERIAL_MISMATCH_PHOTO`, `DUPLICATE_SERIAL_CLAIM`, `MANIPULATION_ATTEMPT` force at least Medium; merges AI signals from evidence/decision outputs; exposes `AssessAtIntakeAsync(CaseContext, IntakeResult)` (signals available without evidence analysis: `PRODUCT_NOT_IN_CATALOG`, `DUPLICATE_SERIAL_CLAIM`, `EVIDENCE_REUSED` from upload hashes; stored with `stage = Intake`) and `AssessFullAsync(...)` (adds evidence-dependent signals; `stage = Full`); persists `adjudication.risk_assessments`
- [ ] T065 [P] [US1] Implement `src/Warranty.AI.Harness/Agents/DecisionAgent.cs`: assemble context (case facts, intake, evidence findings, policy assessment and `POL-n` clauses, deterministic coverage results, risk signals) via `ContextBuilder`, run the `adjudication` route with tools `claim_history_lookup`, `search_global_knowledge` and the `decision-recommendation` schema, validate with `SchemaValidator` and `ReferenceRegistry`, persist `adjudication.recommendations` (invalid → `is_valid=false` with errors; raw output stored after redaction)
- [ ] T066 [US1] Implement `src/Warranty.Guardrails/GuardrailEngine.cs`, `Pipeline/GuardrailInput.cs`, `Pipeline/Checks/*.cs` and `ApprovedAction.cs` (internal constructor): ordered checks with the codes from data-model.md (`SCHEMA_VALID` … `ACTOR_AUTHORIZED`) recording expected/actual/message; disposition rules exactly as the table in contracts/agents-and-tools.md; escalation reasons as human-readable strings; issues `ApprovedAction` for `FinalizeApproved`, `FinalizeRejected`, `RequestInformation` or `EscalateToReview`
- [ ] T067 [US1] Implement `src/Warranty.Application/Actions/ActionExecutor.cs` (`IActionExecutor.ExecuteAsync(ApprovedAction)`): `FinalizeApproved` → claim `Approved` (decided by System, explanation = recommendation's claimant explanation), then `service_network_lookup` → `create_repair_request` → `notify_customer` through the simulated integration ports; `FinalizeRejected` → `Rejected` + notification; `EscalateToReview` → `UnderReview`; `RequestInformation` → `PendingInformation` with requested items; trail entries `AutoApproved`/`AutoRejected`/`EscalatedToReview`/`InformationRequested`/`ActionExecuted`
- [ ] T068 [US1] Implement `src/Warranty.AI.Harness/Execution/AdjudicationRunner.cs` (`IAdjudicationRunner`) and register it in `src/Warranty.AI.Harness/DependencyInjection.cs`: create/resume the run for `(claimId, round)`, `LoadCase → Intake → (Evidence ‖ Policy) → Risk → Decision → Guardrails → Action`, persist each step's output and `current_step` checkpoint (resume skips completed steps), write trail entries `IntakeValidated`, `ClaimExtracted`, `CustomerVerified`, `ProductIdentified`, `PolicyRetrieved`, `EvidenceAnalyzed`, `CoverageAssessed`, `RiskEvaluated`, `AiRecommended`, `GuardrailsEvaluated`, spans per step on `ActivitySource("Warranty.AI.Harness")`
- [ ] T069 [US1] Implement the decision trace read model in `src/Warranty.Application/Trace/DecisionTraceQuery.cs` and `GET /api/claims/{claimId}/trace` in `src/Warranty.Api/Endpoints/Trace/TraceEndpoints.cs` (policy `ReviewerOrAuditor`): chronological entries with summaries, correlation IDs and step details, returning `DecisionTrace` per contracts/rest-api.openapi.yaml
- [ ] T070 [P] [US1] Build the claimant submission page `src/web/src/features/claimant/SubmitClaimPage.tsx` and reusable `src/web/src/features/claimant/ClaimForm.tsx` (customer, product, purchase, description, invoice and 1–8 photos, client-side validation mirroring the API, shows the claim reference and the contact to use for access on success)
- [ ] T071 [P] [US1] Build `src/web/src/features/claimant/AccessClaimPage.tsx` (reference + email/phone → claimant token kept in memory) and `src/web/src/features/claimant/ClaimStatusPage.tsx` (polls `GET /api/public/claims/{reference}` until a non-transient status; shows status, outcome explanation, requested items)
- [ ] T072 [P] [US1] Build the staff "new claim" page `src/web/src/features/claims/NewClaimPage.tsx` for `claims-agent` reusing `ClaimForm.tsx` and posting to `POST /api/claims`

**Checkpoint**: US1 complete — S1 and S2 pass end to end; MVP demonstrable

---

## Phase 4: User Story 2 - Tenant-specific outcomes and strict tenant isolation (Priority: P1)

**Goal**: The same claim type is decided differently by Aurora and Borealis according to each tenant's own, version- and region-correct policy; no tenant can see or influence another tenant's data.

**Independent Test**: S3, S4 (both tenants), S9 and S12 pass; the isolation suite reports zero cross-tenant rows, chunks or blobs (SC-003).

### Tests for User Story 2 ⚠️ write first, ensure they fail

- [ ] T073 [P] [US2] Write retrieval integration tests in `tests/Warranty.IntegrationTests/Knowledge/PolicyRetrievalTests.cs`: purchase 2026-03-01 at Aurora selects `AUR-WP` v1 only (battery 6 months) and never v2; EU vs NA region filtering; category filter; `NoApplicablePolicy` for a purchase before any effective date; `AmbiguousPolicyVersion` with a temporary overlapping test version; Aurora context never returns `tenant-borealis` chunks even for identical query text; a forged chunk from another namespace triggers the post-retrieval assertion and a `RETRIEVAL_SCOPE_VIOLATION` event
- [ ] T074 [P] [US2] Write cross-tenant API tests in `tests/Warranty.IntegrationTests/Isolation/CrossTenantAccessTests.cs` (S12): as each Aurora staff user call every staff endpoint with Borealis claim/evidence IDs → 404 ProblemDetails, no Borealis data in the body, one `CROSS_TENANT_ACCESS_DENIED` event each; claimant token for an Aurora claim used with Host `borealis.localhost` → 401/404; tool invocation with a Borealis `EV-n` is rejected
- [ ] T075 [P] [US2] Generate evidence and add `seed/golden/scenarios.json` entries plus replay fixtures in `tests/fixtures/ai-recordings/` for S3 (Aurora EU, 18 months), S4-aurora and S4-borealis (accidental screen crack, 10 months), S9 (Aurora battery, purchased 2026-03-01), `US2-not-in-catalog` (unknown serial) and `US2-reuse-across-tenants` (a photo already used in a Borealis claim submitted to Aurora)
- [ ] T076 [US2] Write scenario tests in `tests/Warranty.IntegrationTests/Scenarios/US2_TenantSpecificOutcomesTests.cs`: S3 → Approved citing EU clause only; S4 → Aurora Rejected citing the accidental-damage exclusion and Borealis Approved citing its accidental-damage clause, each citing only its own tenant's clauses; S9 → Rejected citing v1 battery clause; not-in-catalog → `UnderReview` with `PRODUCT_NOT_IN_CATALOG` and no claim value; cross-tenant photo reuse → no `EVIDENCE_REUSED` signal at Aurora

### Implementation for User Story 2

- [ ] T077 [P] [US2] Implement `src/Warranty.Guardrails/Rules/AccidentalDamageRule.cs` and extend `CoverageWindowCalculator.cs`: accidental damage covered only when `accidentalDamage.covered`, claim date ≤ purchase date + `windowMonths`, and prior approved accidental claims for the serial (from `claim_history_lookup` counts) < `maxIncidents`; expose results to `warranty_lookup` and the guardrail conflict/grounding checks
- [ ] T078 [US2] Implement cross-tenant denial handling in `src/Warranty.Api/Tenancy/CrossTenantGuard.cs` (endpoint filter converting not-found tenant-scoped lookups into 404 ProblemDetails) and `src/Warranty.Infrastructure/Persistence/Sql/003_security_functions.sql` (`SECURITY DEFINER` function `audit.exists_in_other_tenant(kind text, id uuid, tenant uuid) returns boolean`, callable by `warranty_app`) so a `CROSS_TENANT_ACCESS_DENIED` security event is written without returning any data
- [ ] T079 [US2] Handle products/serials missing from the tenant catalog end to end in `src/Warranty.AI.Harness/Agents/IntakeAgent.cs` and `src/Warranty.AI.Harness/Agents/Risk/RiskAssessor.cs`: never look up other tenants, record `PRODUCT_NOT_IN_CATALOG`, skip policy category filters only to the tenant's catalog-independent clauses, and make the guardrail input carry "no claim value" so `PRODUCT_IN_CATALOG` and `CLAIM_VALUE_WITHIN_LIMIT` fail → `HumanReview`
- [ ] T080 [P] [US2] Implement `GET /api/me` in `src/Warranty.Api/Endpoints/Reference/MeEndpoints.cs` and the tenant banner `src/web/src/app/TenantBanner.tsx` (claimant area: `GET /api/public/tenant` display name; staff area: `/api/me` tenant name and roles)
- [ ] T081 [P] [US2] Implement `GET /api/policies` in `src/Warranty.Api/Endpoints/Reference/PolicyEndpoints.cs` (tenant's policy versions with effective dates and regions) and `src/web/src/features/claims/PoliciesPage.tsx`

**Checkpoint**: US1 and US2 complete — both tenants decide the same claim type differently; isolation suite green

---

## Phase 5: User Story 3 - Human review of escalated claims (Priority: P2)

**Goal**: High-value, low-confidence, risky, conflicting or always-review claims land in the tenant's review queue with reasons; a reviewer sees everything and approves, rejects or requests information, accepting or overriding the AI.

**Independent Test**: S5 (high value → queue), S11 (override reject with mandatory justification, AI recommendation unchanged), S13 (always-review category).

### Tests for User Story 3 ⚠️ write first, ensure they fail

- [ ] T082 [P] [US3] Write escalation unit tests in `tests/Warranty.UnitTests/Guardrails/EscalationRulesTests.cs`: each trigger alone yields `HumanReview` with the right reason — value above limit, confidence below minimum, risk Medium, risk High, always-review category, AI `HUMAN_REVIEW`, `NoApplicablePolicy`, `AmbiguousPolicyVersion`, coverage `UNDETERMINED` / agent ambiguity flag
- [ ] T083 [P] [US3] Generate evidence and add scenario entries and replay fixtures for S5 (Aurora `AUR-BOOK15`, defect, 3 months), S11 (reuses S5 claim), S13 (Borealis `BOR-OVEN60`) in `seed/golden/scenarios.json` and `tests/fixtures/ai-recordings/`
- [ ] T084 [US3] Write integration tests in `tests/Warranty.IntegrationTests/Scenarios/US3_HumanReviewTests.cs`: S5 → `UnderReview`, appears in `reviewer.aurora` queue with reason "claim value above auto-approval limit" and not in `reviewer.borealis` queue; claim detail returns all FR-033 sections and an ETag; S11 reject without justification → 400, with justification → `Rejected` by Reviewer, `overridesAi=true`, recommendation row unchanged, trail `ReviewerDecided` with reviewer identity; second decision with stale `If-Match` → 412, on a non-UnderReview claim → 409; S13 → reasons include always-review category and value above limit; reviewer `RequestInformation` → `PendingInformation` with items

### Implementation for User Story 3

- [ ] T085 [P] [US3] Implement claim queries in `src/Warranty.Application/Claims/ClaimQueries.cs` and endpoints `GET /api/claims` (paged, status filter, `AnyStaff`), `GET /api/claims/{claimId}` (`ClaimDetail` with latest evaluation: validation, extraction, evidence findings with `EV-n`, policy references with excerpts and versions, policy assessment, risk, recommendation, guardrail checks and disposition, review decisions, and the Policy and Evidence agents' confidence values; ETag from row version) in `src/Warranty.Api/Endpoints/Claims/ClaimEndpoints.cs`, and `GET /api/claims/{claimId}/evidence/{evidenceId}/content` streaming via `IDocumentStore` in `src/Warranty.Api/Endpoints/Claims/EvidenceEndpoints.cs`
- [ ] T086 [P] [US3] Implement `src/Warranty.Application/Review/ReviewQueueQuery.cs` and `GET /api/review-queue` (`ClaimsReviewer`) in `src/Warranty.Api/Endpoints/Review/ReviewEndpoints.cs`: claims in `UnderReview` with product, claim value, AI decision, confidence, risk level, escalation time and reasons, oldest first
- [ ] T087 [US3] Implement `src/Warranty.Application/Review/RecordReviewDecision.cs` and extend `src/Warranty.Application/Actions/ActionExecutor.cs` with `ExecuteReviewerDecisionAsync`: claim must be `UnderReview`; `If-Match` row-version check; `overridesAi` = decision differs from the AI recommendation; justification required when overriding or rejecting (10–2,000 chars); requested items required for `RequestInformation`; claimant explanation = AI claimant explanation when the reviewer agrees with the AI, otherwise a fixed per-outcome template; finalize Approved/Rejected (decided by Reviewer, simulated repair request/notification on approval) or move to `PendingInformation`; trail `ReviewerDecided`
- [ ] T088 [US3] Implement `POST /api/claims/{claimId}/review-decisions` (`ClaimsReviewer`, `If-Match` header, 201 `ReviewDecision`, 400/404/409/412 ProblemDetails) in `src/Warranty.Api/Endpoints/Review/ReviewEndpoints.cs`
- [ ] T089 [P] [US3] Build `src/web/src/features/claims/ClaimsListPage.tsx`, `src/web/src/features/claims/ClaimDetailPage.tsx` and `src/web/src/features/claims/EvidenceViewer.tsx` (authorized image/PDF display; claim data, extracted data, policy excerpts with clause key/version/dates, recommendation, confidence, risk signals, reasoning, guardrail checks with pass/fail and reasons; the Policy and Evidence agents' confidence shown next to their findings)
- [ ] T090 [P] [US3] Build `src/web/src/features/review/ReviewQueuePage.tsx` and `src/web/src/features/review/ReviewDecisionForm.tsx` (approve / reject / request information; justification field required and enforced when overriding the AI or rejecting; requested-item picker; sends `If-Match`; shows 409/412 conflicts)

**Checkpoint**: US1–US3 complete — escalated claims are resolved by reviewers with a full audit record

---

## Phase 6: User Story 4 - Guardrails prevent unsafe automated outcomes (Priority: P2)

**Goal**: No claim is finalized on the AI's say-so when the output is invalid, unavailable, manipulated, contradicts deterministic checks, or cites unissued references; manipulation attempts are flagged and never auto-approved.

**Independent Test**: S7, S8, S10 plus invalid-output, refusal, truncation, deadline and tool-scope probes — none auto-finalized, each records the failing check.

### Tests for User Story 4 ⚠️ write first, ensure they fail

- [ ] T091 [P] [US4] Write unit tests in `tests/Warranty.UnitTests/Harness/InjectionDetectorTests.cs` (phrases from `seed/global/injection-phrases.md` in description, invoice text and image text, case/spacing variants, benign text not flagged) and `tests/Warranty.UnitTests/Guardrails/SafetyGuardrailTests.cs` (AI APPROVE vs expired deterministic window → `HumanReview` "AI recommendation conflicts with coverage-period check"; AI cites an exclusion the deterministic terms don't contain → conflict; unissued `POL-n` → `REFERENCES_VALID` failed; any manipulation signal → never `AutoApprove`)
- [ ] T092 [P] [US4] Add scenario entries, evidence and replay fixtures for S7-serial (photo serial ≠ claim serial), S7-reuse (photo reused from an earlier Aurora claim), S8 (manipulative description), S10 (fixtures returning `failure: ProviderError`), `US4-invalid-output` (decision output violating the schema twice), `US4-refusal` (`stop: Refused`), `US4-truncated` (`stop: Truncated` twice) and `US4-consequential-tool` (model requests `create_repair_request`) in `seed/golden/scenarios.json` and `tests/fixtures/ai-recordings/`
- [ ] T093 [US4] Write integration tests in `tests/Warranty.IntegrationTests/Scenarios/US4_GuardrailSafetyTests.cs`: each scenario ends `UnderReview` with the expected failed check or reason; S8 has `MANIPULATION_ATTEMPT` and failed `NO_MANIPULATION`; S10/refusal/truncation have `AiStepFailed` trail entries and `failure_reason`, claimant view shows "Under Review"; invalid output made exactly one corrective turn; consequential tool request denied with `TOOL_SCOPE_VIOLATION` and no repair request row

### Implementation for User Story 4

- [ ] T094 [P] [US4] Implement `src/Warranty.AI.Harness/Safety/InjectionDetector.cs` (normalized phrase/pattern matching from the global injection phrase list, applied to description, extracted invoice text and photo observations) and wire it into `src/Warranty.AI.Harness/Agents/Risk/RiskAssessor.cs` together with model flags `containsInstructionsToSystem` and `manipulationDetected` to raise `MANIPULATION_ATTEMPT`
- [ ] T095 [P] [US4] Implement AI failure handling in `src/Warranty.AI.Harness/Execution/AgentTurnLoop.cs` and `src/Warranty.AI.Harness/Execution/AdjudicationRunner.cs`: map `AiFailure`/`Refused`/`Timeout` to `AgentStatus`; one corrective turn appending validation errors for `InvalidOutput`; one retry with doubled `MaxTokens` for `Truncated`; 4-minute run deadline via linked cancellation; on any unrecoverable AI step write `AiStepFailed`, set `failure_reason`, mark the recommendation invalid/missing and let the guardrail engine produce `HumanReview`; partial photo-analysis failure fails the Evidence step
- [ ] T096 [P] [US4] Implement `src/Warranty.Guardrails/Rules/ConflictRules.cs` and register it in the engine's `NO_CONFLICTS` and `COVERAGE_WINDOW_AGREES` checks: AI coverage vs deterministic window, AI-cited exclusions vs the version's `exclusions`, AI `APPROVE` with failed invoice consistency, AI product/serial assumptions vs catalog

**Checkpoint**: US1–US4 complete — unsafe AI output never finalizes a claim

---

## Phase 7: User Story 5 - Incomplete claims and requests for more information (Priority: P3)

**Goal**: Claims missing required information are paused with a specific list of missing items; the submitter supplements the same claim, which is fully re-evaluated with history preserved.

**Independent Test**: S6 — no invoice → `PendingInformation` listing "legible invoice"; supplement → round 2 → outcome; trail retains both rounds.

### Tests for User Story 5 ⚠️ write first, ensure they fail

- [ ] T097 [P] [US5] Add scenario entries, evidence and replay fixtures for S6-round1 (no invoice), S6-round2 (invoice supplied), `US5-unclear-photos` (AI `REQUEST_MORE_INFORMATION` asking for `PHOTO_OF_DAMAGE`) and `US5-missing-and-high-value` (Aurora `AUR-BOOK15` without invoice) in `seed/golden/scenarios.json` and `tests/fixtures/ai-recordings/`
- [ ] T098 [US5] Write integration tests in `tests/Warranty.IntegrationTests/Scenarios/US5_RequestInformationTests.cs`: S6 round 1 → `PendingInformation`, requested items include the invoice, no Evidence/Policy/Decision model calls made; claimant supplement via `POST /api/public/claims/{reference}/supplements` → round 2 completes; trail contains both runs; unclear photos → `PendingInformation` asking for damage photos; missing + high value → `UnderReview` (FR-010/FR-028 precedence); supplement on an `Approved` claim → 409

### Implementation for User Story 5

- [ ] T099 [US5] Implement the intake short-circuit in `src/Warranty.AI.Harness/Execution/AdjudicationRunner.cs` and `src/Warranty.Application/Claims/RequestedItemCatalog.cs`: when intake finds missing items, skip Evidence/Policy/Decision and evaluate guardrails with `REQUIRED_INFO_COMPLETE` failed → `RequestInformation`, except when an escalation condition determinable without the missing information also holds (value above limit, always-review category, or risk ≥ Medium from `RiskAssessor.AssessAtIntakeAsync`) → `HumanReview` (FR-010/FR-028 precedence); map item codes to claimant-friendly text
- [ ] T100 [US5] Implement `src/Warranty.Application/Claims/SupplementClaim.cs`: only for `PendingInformation`; validates files as in SubmitClaim; `AddSupplement()` increments the round; stores evidence with the new round; enqueues a job for the new round; trail `SupplementReceived`
- [ ] T101 [US5] Implement `POST /api/public/claims/{reference}/supplements` (claimant token) in `src/Warranty.Api/Endpoints/Public/ClaimantEndpoints.cs` and `POST /api/claims/{claimId}/supplements` (`ClaimsAgent`) in `src/Warranty.Api/Endpoints/Claims/ClaimEndpoints.cs`, returning 202 or 409 for non-pending claims
- [ ] T102 [P] [US5] Build `src/web/src/features/claimant/SupplementForm.tsx` (shown on `ClaimStatusPage` while `PendingInformation`, lists requested items, uploads invoice/photos/note) and a supplement action for claims agents in `src/web/src/features/claims/ClaimDetailPage.tsx`

**Checkpoint**: US1–US5 complete — incomplete claims recover without manual triage

---

## Phase 8: User Story 6 - Decision trail inspection (Priority: P3)

**Goal**: Reviewers and auditors see a complete, chronological, tamper-evident decision trace including AI model calls, tool calls, retrieval queries, guardrail results and human interventions.

**Independent Test**: S14 — traces of an auto-approved, an auto-rejected and an overridden claim contain every FR-038 element with `hashChainValid: true`; edits are impossible.

### Tests for User Story 6 ⚠️ write first, ensure they fail

- [ ] T103 [US6] Write integration tests in `tests/Warranty.IntegrationTests/Scenarios/US6_DecisionTrailTests.cs`: traces for S1, S2 and S11 contain submission, evidence references, extracted data, validation, retrieved policy references, evidence findings, risk, recommendation with confidence and reasoning, guardrail checks, routing, human review (S11) and final outcome, in `seq` order; AI call summaries include model, prompt version, tokens, latency and cost; `hashChainValid` true; after a direct owner-level SQL edit of one entry `hashChainValid` is false; `claims-agent` gets 403

### Implementation for User Story 6

- [ ] T104 [US6] Extend `src/Warranty.Application/Trace/DecisionTraceQuery.cs`: attach `aiops.model_calls`, `aiops.tool_calls` and `aiops.rag_queries` (namespaces, filters, result clause keys) to the matching steps by run and time, include guardrail check lists and review decisions, compute `integrity.hashChainValid` with `HashChainVerifier`
- [ ] T105 [P] [US6] Build `src/web/src/features/trace/DecisionTracePage.tsx` and `src/web/src/features/trace/TraceEntryDetails.tsx`: vertical timeline (Claim received → … → final outcome), expandable AI call/tool/RAG details with tokens, cost and latency, integrity badge, link from `ClaimDetailPage`
- [ ] T106 [P] [US6] Add auditor navigation in `src/web/src/app/routes.tsx` and `src/web/src/features/claims/ClaimsListPage.tsx`: read-only claims list for `auditor` with trace links; hide review actions for auditors

**Checkpoint**: All user stories independently functional

---

## Phase 9: Polish & Cross-Cutting Concerns

**Purpose**: Evaluation, hardening, frontend tests and end-to-end validation

- [ ] T107 [P] Build the golden dataset `seed/golden/golden-claims.json` with ≥20 labeled claims per tenant (mix of approve, reject, request information and each escalation reason; expected disposition, expected recommendation, expected cited clause keys, expected extraction fields) and generate their evidence with `tools/Warranty.EvidenceGenerator` into `seed/evidence/golden/`
- [ ] T108 Implement the evaluation runner in `tests/Warranty.Evaluation/Program.cs` and `tests/Warranty.Evaluation/Metrics/*.cs`: modes `--mode replay|live` and `--record`; isolated evaluation database (Testcontainers) seeded like production; claim history reset per case; metrics extraction field accuracy, retrieval recall@k of expected clause keys, recommendation and disposition accuracy, escalation recall, Brier score and accuracy by confidence band, unsupported-reference rate, tokens, cache-read tokens and cost; human override rate query over `review.review_decisions`; report `artifacts/eval/{timestamp}/report.md` and `report.json`
- [ ] T109 [P] Add rate-limiting policies in `src/Warranty.Api/RateLimiting/RateLimitPolicies.cs` and apply them: `POST /api/public/claims` 10 per hour per IP, `POST /api/public/claims/access` 5 per 15 minutes per IP + reference, 429 ProblemDetails with `Retry-After`
- [ ] T110 [P] Harden uploads in `src/Warranty.Infrastructure/Storage/UploadSanitizer.cs`: verify magic bytes against the declared type, re-encode images to strip EXIF/location metadata before storage, reject encrypted or script-bearing PDFs
- [ ] T111 [P] Add security headers and a CORS policy limited to `localhost`, `aurora.localhost` and `borealis.localhost` origins in `src/Warranty.Api/Security/SecurityHeaders.cs`
- [ ] T112 [P] Write frontend tests in `src/web/tests/`: `ClaimForm.test.tsx` (validation), `AccessClaimPage.test.tsx` (generic error on 401), `ReviewDecisionForm.test.tsx` (justification required when overriding/rejecting), `DecisionTracePage.test.tsx` (renders steps and integrity badge) using MSW handlers
- [ ] T113 [P] Add an Aspire smoke test in `tests/Warranty.IntegrationTests/Smoke/AppHostSmokeTests.cs` (trait `Category=Smoke`) that boots the AppHost in replay mode and completes S1
- [ ] T114 Run S1 with live models five times, record p50/p95 time-to-decision against SC-001 (< 2 minutes) and cache-read tokens in `artifacts/validation/performance.md`; tune route effort or parallelism if the target is missed
- [ ] T115 Update `CLAUDE.md` and `specs/001-ai-claim-adjudication/quickstart.md` with the verified build, run, test and evaluation commands
- [ ] T116 Run the full quickstart validation S1–S15 (UI and headless) and record results in `artifacts/validation/quickstart-results.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies
- **Foundational (Phase 2)**: Depends on Setup — BLOCKS all user stories
- **US1 (Phase 3)**: Depends on Foundational — the adjudication pipeline every other story exercises
- **US2 (Phase 4)**: Depends on US1 for scenario tests (T076); its isolation tests (T073, T074) and T077–T081 can start right after Foundational
- **US3 (Phase 5)**: Depends on US1 (escalations come from the runner and guardrail engine)
- **US4 (Phase 6)**: Depends on US1; independent of US2/US3
- **US5 (Phase 7)**: Depends on US1; reviewer-initiated information requests also need US3 (T087)
- **US6 (Phase 8)**: Depends on US1; the override trace test (S11) needs US3
- **Polish (Phase 9)**: Depends on all desired stories

### User Story Dependencies (completion order)

```text
Setup → Foundational → US1 ─┬─► US2
                            ├─► US3 ─┬─► US5
                            │        └─► US6
                            └─► US4
```

### Within Each User Story

- Tests and fixtures first; scenario tests must fail before implementation
- Deterministic rules (Guardrails) and schemas before agents
- Agents before runner; runner before endpoints that depend on outcomes
- Backend endpoints before the UI that calls them

### Parallel Opportunities

- Setup: T006 and T007 alongside T003–T005
- Foundational: T008–T011 (domain) in parallel; T014, T016, T018–T021 in parallel after T012/T013; T027, T029, T030 in parallel with T026/T028; T036, T037, T040–T044 in parallel
- US1: T047–T051 (tests/fixtures) in parallel; T053–T055 in parallel; agents T062–T065 in parallel after T060/T061; UI T070–T072 in parallel with backend work
- US2: T073–T075 in parallel; T077, T080, T081 in parallel
- US3: T082, T083 in parallel; T085, T086 in parallel; T089, T090 in parallel
- US4: T091, T092 in parallel; T094–T096 in parallel
- After US1: US2, US3 and US4 can proceed in parallel by different developers

---

## Parallel Example: User Story 1

```text
# Tests and fixtures together:
Task: "T047 Guardrail disposition unit tests in tests/Warranty.UnitTests/Guardrails/DispositionRulesTests.cs"
Task: "T048 Coverage window unit tests in tests/Warranty.UnitTests/Guardrails/CoverageWindowTests.cs"
Task: "T049 Reference registry and schema validator tests in tests/Warranty.UnitTests/Harness/"
Task: "T050 Synthetic evidence generator in tools/Warranty.EvidenceGenerator/Program.cs"
Task: "T051 S1/S2 scenarios and replay fixtures in seed/golden/ and tests/fixtures/ai-recordings/"

# Agents together (after tools T060 and IntakeAgent T061):
Task: "T062 EvidenceAgent in src/Warranty.AI.Harness/Agents/EvidenceAgent.cs"
Task: "T063 PolicyAgent in src/Warranty.AI.Harness/Agents/PolicyAgent.cs"
Task: "T064 RiskAssessor in src/Warranty.AI.Harness/Agents/Risk/RiskAssessor.cs"
Task: "T065 DecisionAgent in src/Warranty.AI.Harness/Agents/DecisionAgent.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Phase 1 Setup → Phase 2 Foundational (`aspire run` brings everything up, seeds and indexes)
2. Phase 3 US1 → **STOP and VALIDATE** S1 and S2 (replay tests, then one live run)
3. Demo: claim submitted on `aurora.localhost`, auto-decided with cited clauses and a trace

### Incremental Delivery (one-week PoC)

| Day | Scope |
|-----|-------|
| 1 | Setup + Foundational (persistence, RLS, gateway, knowledge, harness framework, seeding) |
| 2–3 | US1 (MVP) |
| 4 | US2 (two tenants, isolation suite) + US4 (guardrail safety) |
| 5 | US3 (human review) + US5 (request information) |
| 6 | US6 (trace UI) + evaluation runner + golden dataset |
| 7 | Hardening, performance check, full quickstart validation |

### Parallel Team Strategy

After Foundational and US1: Developer A takes US2 + US6, Developer B takes US3 + US5, Developer C
takes US4 + the evaluation runner.

---

## Notes

- [P] tasks = different files, no dependency on an incomplete task; [USn] maps a task to its story
- Checklist conflicts CHK020 (FR-010/FR-028 precedence, T099), CHK021 (FR-006 allows platform-owned
  global knowledge), CHK022/CHK025 (always-review categories in FR-026–FR-028 and SC-004) and CHK024
  (simulated repair request and notification, spec Assumptions) were resolved in spec.md on
  2026-10-02; the reviewer still owns marking them `[x]` in `checklists/guardrails-isolation.md`.
- `/speckit-implement` treats unchecked checklist items as a gate and will ask before proceeding
- Live AI runs cost money; all automated tests use the replay provider
- Commit after each task or logical group; stop at any checkpoint to validate a story
