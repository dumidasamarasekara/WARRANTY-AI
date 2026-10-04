# Implementation Plan: AI-Powered Warranty Claim Adjudication (Multi-Tenant PoC)

**Branch**: `001-ai-claim-adjudication` (spec directory; git work on per-task / `docs/` branches, see CLAUDE.md) | **Date**: 2026-10-02, updated 2026-10-03 for spec clarification session 2026-10-03 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-ai-claim-adjudication/spec.md`

## Summary

Build a one-week, end-to-end vertical slice of a multi-tenant warranty administration platform:
two tenants with deliberately different policies; claim submission with invoice and photos;
tenant-aware, version-correct policy retrieval; multi-agent AI analysis (Intake, Evidence, Policy,
Decision + Risk); deterministic guardrails; automatic approval/rejection or human review; and an
immutable, explorable decision trace.

Technical approach: a .NET 10 modular monolith (ASP.NET Core REST API + in-process worker)
orchestrated locally by .NET Aspire on Podman, with a React/TypeScript/Vite SPA. A first-class
**AI Harness** runs a fixed, code-defined workflow and gives each agent a bounded, permission-checked
tool loop. All model access goes through a provider-agnostic **AI Gateway** (primary provider:
Anthropic via the official C# SDK — `claude-haiku-4-5` for extraction, `claude-opus-5-5` for
vision and adjudication; local Ollama embeddings). **Tenant-aware RAG** uses pgvector in a separate
knowledge database partitioned by namespace (`global`, `tenant-*`) with metadata-first,
effective-date-correct retrieval. Tenant isolation is enforced in four places that do not depend on
the LLM: tenant resolution from trusted context, EF Core query filters, PostgreSQL row-level
security, and per-tenant knowledge partitions. A deterministic **guardrail engine** is the only
component that can authorize a consequential action. Details and rationale:
[research.md](./research.md).

The 2026-10-03 clarifications are designed in research R23–R25: any risk signal blocks automatic
approval and rejection; a claim a reviewer sent back for information always returns to a reviewer;
at most two automatic information requests per claim; reviewers write the claimant-facing
explanation separately from their internal justification; a duplicate claim is another claim for
the same serial that is open or was finalized in the last 90 days. The second round of
clarifications is designed in R26–R30: exclusion-based automatic rejections are confirmed against
structured exclusion codes and photo damage types; fixed evidence-matching tolerances; no customer
identifiers in prompts and no-training providers only; no reviewer may decide a claim they
submitted; append-only security events that tenant auditors can view without learning anything
about other tenants.

## Technical Context

**Language/Version**: C# 14 / .NET 10 (backend); TypeScript (strict) / React 19 (frontend)

**Primary Dependencies**: ASP.NET Core 10 (Minimal APIs, built-in OpenAPI, JWT bearer auth, rate
limiting); EF Core 10 + Npgsql; Pgvector for .NET; Anthropic official C# SDK (`Anthropic`);
Microsoft.Extensions.AI (embedding abstraction); Azure.Storage.Blobs; .NET Aspire 13 (AppHost,
ServiceDefaults, PostgreSQL, Azure Storage emulator, Keycloak, Ollama, Vite integrations);
OpenTelemetry; Vite, React Router, TanStack Query, oidc-client-ts / react-oidc-context,
openapi-typescript / openapi-fetch; self-hosted `@fontsource` fonts (Fraunces, Inter, JetBrains
Mono) for the WarrantyOS design system ([ui-design.md](./ui-design.md))

**Storage**: PostgreSQL 17 (pgvector image): database `warranty` (transactional, schemas per
domain) and database `knowledge` (RAG chunks, namespace-partitioned); Azurite (Azure Blob API) for
invoices, photos and knowledge source documents

**Testing**: xUnit v3, Shouldly, NSubstitute, NetArchTest, Testcontainers (Podman),
WebApplicationFactory, replay-based AI provider; Vitest + React Testing Library + MSW; separate
evaluation runner (`tests/Warranty.Evaluation`)

**Target Platform**: Local developer workstation (Windows 11 / macOS / Linux) with Podman 5;
Linux containers. Cloud deployment out of scope for the PoC.

**Project Type**: Web application — REST API + background worker + SPA

**Performance Goals**: Clear-cut claim reaches a final automated decision in < 2 minutes
(SC-001; target p50 ≈ 60 s with Evidence and Policy agents in parallel); reviewer screens load
in < 2 s; demonstration volume only

**Constraints**: One-week delivery; no real enterprise integrations (CRM, ERP, payment, service
network, notifications are simulated); synthetic data only; LLM never authorizes a consequential
action; tenant isolation must not rely on the LLM; business code must not reference a vendor AI SDK

**Scale/Scope**: 2 seeded tenants (architecture supports N); tens to hundreds of claims per
tenant; ~9 SPA screens; 4 agents + 1 risk capability; 10 tools (8 business tools + 2 knowledge
search tools); ~15 REST endpoints

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | How this plan complies | Pre-research | Post-design |
|---|-----------|------------------------|:------------:|:-----------:|
| I | Security-First: Tenant Isolation | Tenant from trusted context only (R9); EF query filters + PostgreSQL RLS with non-owner role (R8); per-namespace knowledge partitions + RLS + post-retrieval assertion (R6, R7); per-tenant blob containers with API-mediated access (R11); duplicate/reuse checks scoped to tenant; prompt cache prefix holds no tenant data (R16); cross-tenant access → 404 + a tenant-visible `ACCESS_DENIED` indistinguishable from an unknown ID, plus an operator-only cross-tenant event (R30); no customer identifiers in prompts, no-training providers only (R28) | PASS | PASS |
| II | AI Does Not Directly Control Critical Operations | Fixed, code-defined workflow (R2); LLM agents hold read-only tools only; guardrail engine has no AI dependency and is the only issuer of `ApprovedAction`; `ActionExecutor` accepts only `ApprovedAction` (R13); architecture tests enforce it; exclusion-based automatic rejections need a structured exclusion code matched by photo evidence (R26) | PASS | PASS |
| III | Explainable Decisions | Structured recommendation with evidence refs, policy refs (clause key + version + effective dates), confidence, reasoning summary (contracts/schemas); harness-issued reference IDs make citations verifiable (R7); persisted with the run, not reconstructed; every final outcome has a claimant-facing explanation — the AI's for automatic decisions, the reviewer's own for human decisions, screened for risk/fraud disclosure (R25) | PASS | PASS |
| IV | Human-in-the-Loop | Escalation rules evaluated deterministically (R13); any risk signal blocks automatic approval and rejection (R23); claims a reviewer sent back always return to a reviewer, and a third automatic information request escalates instead (R24); review queue, reviewer decision with mandatory justification on override/reject; no reviewer may decide a claim they submitted (R29); AI recommendation retained unchanged; human decision written to the decision trail | PASS | PASS |
| V | Tenant-Aware by Design | `ITenantContext` established in middleware/worker scope before any business code; propagated to data (RLS session variable), retrieval (namespace), tools (injected, no tenant parameter) and traces | PASS | PASS |
| VI | Model Independence | `IAiGateway` port; task-based routing in configuration; Anthropic SDK referenced only by `Warranty.AI.Gateway` (architecture test); embeddings via a second provider; replay provider for tests | PASS | PASS |
| VII | Observable AI | OpenTelemetry with GenAI attributes + persisted model calls, tool calls, RAG queries, guardrail results, human overrides, costs; decision trace UI (R16) | PASS | PASS |
| VIII | PoC-First Architecture | Modular monolith, one API process, in-process worker, Postgres-backed job queue, pgvector instead of a separate vector DB, simulated integrations; extension seams listed below. Tensions recorded in Complexity Tracking | PASS (justified) | PASS (justified) |

Development workflow gate (constitution): this plan includes the required explicit check against
all eight principles. No principle is violated; complexity items are justified below.

## Architecture

### Logical layers and request flow

```text
 React SPA (claimant portal | staff workspace)
        │ HTTPS, OIDC bearer / claim-scoped token
        ▼
 Application / API layer (Warranty.Api)
   Authentication → Tenant resolution → Authorization → ITenantContext
        │ submit claim = write claim + evidence + job (one transaction) → 202
        ▼
 Claim job worker (in-process BackgroundService, tenant scope per job)
        ▼
 AI Harness (Warranty.AI.Harness) — owns the run lifecycle
   context construction → agent selection → tool execution → model invocation
   → structured-result validation → guardrails → action OR escalation → audit/trace
        │                         │                          │
        ▼                         ▼                          ▼
 Agent capabilities        Tool layer (permissioned,   Tenant-aware RAG (Warranty.Knowledge)
 Intake · Evidence ·       tenant-scoped; read-only    global + tenant namespaces,
 Policy · Decision(+Risk)  for agents)                 metadata-first retrieval
        │                         │                          │
        ▼                         ▼                          ▼
 AI Gateway (Warranty.AI.Gateway)  Business services        Data layer
 routing · prompts · redaction ·   (Application + simulated  PostgreSQL warranty / knowledge,
 rate limits · tokens/cost ·        CRM/ERP/service network/  Azurite blobs
 provider adapters                  notifications)
        ▼
 Anthropic (claude-haiku-4-5, claude-opus-5-5) · Ollama embeddings
        ▼
 Guardrail engine (Warranty.Guardrails, deterministic) → ApprovedAction → ActionExecutor
   or → Human review queue (reviewer decision → ActionExecutor)
        ▼
 Decision trail (append-only) + AI execution records + OpenTelemetry
```

### Adjudication workflow (one run per claim submission round)

| Step | Component | Kind | Output |
|------|-----------|------|--------|
| 1 | Harness: load case | Deterministic | Case context (claim, customer via simulated CRM, product via catalog) |
| 2 | Intake Agent | Deterministic validation + `extraction` model | Validation results; structured problem description. Missing required info → short-circuit to the guardrails: `RequestInformation`, or `HumanReview` when a deterministic escalation applies, the claim was returned by a reviewer, or two automatic requests were already made (R24) |
| 3a | Evidence Agent (parallel with 3b) | `extraction` model (invoice PDF) + `vision` model (photos) + invoice-validation tool | Invoice fields, photo findings, cross-source consistency |
| 3b | Policy Agent (parallel with 3a) | Retrieval (filter-first) + `policy-reasoning` model + warranty-lookup tool | Applicable clauses `[POL-n]`, coverage assessment, structured coverage terms |
| 4 | Risk capability (inside Decision step) | Deterministic signals (open-or-90-day duplicate claims, image hash reuse, date anomalies, catalog mismatch, injection detector) merged with AI-reported signals | Risk signals, score and level — `Low` only with no signal (R23) |
| 5 | Decision Agent | `adjudication` model | Structured recommendation ([contracts/schemas/decision-recommendation.schema.json](./contracts/schemas/decision-recommendation.schema.json)) |
| 6 | Guardrail engine | Deterministic | Check results (incl. loop-state and claimant-text checks) + disposition (`AutoApprove`, `AutoReject`, `RequestInformation`, `HumanReview`) + `ApprovedAction` when allowed |
| 7 | ActionExecutor or review queue | Deterministic | Claim status change and loop counters (R24); final claimant explanation; simulated repair request + notification on approval; or escalation |
| 8 | Audit | Deterministic | Decision trail entries for every step; AI execution records |

Any AI step that fails, times out, is refused, or returns an invalid structure after one corrective
retry ends the run with disposition `HumanReview` (FR-023, FR-031).

### Component responsibilities

| Component | Responsibility | Must not |
|-----------|----------------|----------|
| `Warranty.Api` | HTTP endpoints, authN/Z, tenant resolution middleware, ProblemDetails, rate limiting, hosting the worker; composition root | Contain business rules |
| `Warranty.Domain` | Entities, value objects, enums, state transitions (claim lifecycle), recommendation/disposition types | Reference any infrastructure or AI package |
| `Warranty.Application` | Use cases (submit, supplement, review, queries), ports (`IAiGateway`, `IKnowledgeRetriever`, `IDocumentStore`, integration ports), `ITenantContext`, `ActionExecutor` | Reference provider SDKs or EF Core directly |
| `Warranty.Guardrails` | Deterministic validation pipeline, escalation rules, `ApprovedAction` issuance | Reference AI, harness or gateway projects |
| `Warranty.AI.Harness` | Run lifecycle, workflow, agents, prompts' variables, tool registry and permissions, context assembly (token-aware), execution state, retries, checkpoints | Execute consequential actions; read tenant from model output |
| `Warranty.AI.Gateway` | `IAiGateway` implementation, task routing, prompt templates, redaction, image preparation (downscaling), rate limits, usage/cost, provider adapters (Anthropic, Ollama embeddings, replay) | Hold business rules |
| `Warranty.Knowledge` | Ingestion (clause chunking, embedding, metadata), filtered retrieval, namespace/RLS enforcement | Query without a tenant context |
| `Warranty.Infrastructure` | EF Core DbContexts/migrations, RLS interceptor, repositories, blob store, job queue, audit store | Contain business decisions |
| `Warranty.Integrations.Simulated` | Simulated CRM (customers), ERP (serial registry), service network, repair requests, notifications (outbox), payment stub | Be referenced by Domain/Application (only via ports) |
| `Warranty.MigrationService` | Apply migrations, create roles/RLS, seed tenants, index knowledge, then exit | Run in production request path |
| `web/` | Claimant portal, staff workspace, review UI, decision trace view | Hold secrets; trust its own tenant selection |

### Tenant isolation strategy (summary)

1. **Resolution**: staff → token `tenant_id` claim; claimant → Host → `tenant_channels`; worker →
   job record. Never body, query string, or model output (R9).
2. **Context**: scoped `ITenantContext` (tenant ID, principal, roles, correlation ID); worker uses
   `TenantContextScope` per job.
3. **Data**: EF Core global query filters + PostgreSQL RLS (`FORCE`, non-owner role,
   `app.tenant_id` set per connection) (R8).
4. **Knowledge**: namespace partitions + RLS (`app.kb_namespace`) + filter-first queries +
   post-retrieval assertion (R6, R7).
5. **Documents**: per-tenant blob containers; API-mediated access only (R11).
6. **Tools**: no tenant parameter in any tool schema; harness injects context; per-agent allow-list.
7. **AI context**: case data from the run's claim only; harness-issued `[EV-n]`/`[POL-n]` IDs;
   cache prefix free of tenant data.
8. **Verification**: dedicated isolation test suite (cross-tenant API calls, RLS raw-SQL probes,
   retrieval probes, tool-scope probes) — SC-003.

### Interfaces (summaries; full contracts in `contracts/`)

- **REST API**: [contracts/rest-api.openapi.yaml](./contracts/rest-api.openapi.yaml) — public
  claimant channel (`/api/public/...`) and staff API (`/api/...`).
- **AI Gateway**: [contracts/ai-gateway.md](./contracts/ai-gateway.md) — `IAiGateway`,
  request/response types, routes, model profiles, usage records.
- **RAG**: [contracts/rag.md](./contracts/rag.md) — `IKnowledgeRetriever`, `IKnowledgeIngestor`,
  metadata schema, retrieval rules.
- **Agents and tools**: [contracts/agents-and-tools.md](./contracts/agents-and-tools.md) — `IAgent`,
  `ITool`, tool catalog with permissions, harness execution state.
- **Structured AI outputs**: [contracts/schemas/](./contracts/schemas/) — JSON Schemas for each
  agent's output.
- **Data model**: [data-model.md](./data-model.md).

### User interface design

The SPA implements the **WarrantyOS design system** (Claude Design prototype in
[design/WarrantyOS.dc.html](./design/WarrantyOS.dc.html)), adapted to this feature in
[ui-design.md](./ui-design.md), which is binding for every `src/web` task:

- **Tokens** (colour, tone triples, type scale, spacing, radius, elevation, motion) as CSS custom
  properties in `src/web/src/shared/styles/tokens.css`; CSS modules only, no UI library (R20).
- **Component kit** in `src/web/src/shared/ui/` (buttons, badges, confidence meter, AI panel,
  disposition banner, tables, dialog, form fields, file drop, stepper, timeline, policy citation)
  and the domain-to-visual mapping (claim status, AI decision, disposition, trail actor) in
  `src/web/src/shared/presentation/`, built once (T117) before any page.
- **Actor visual language**: AI output is violet, dashed and labelled "AI", always with its
  confidence; human decisions are blue and solid with the person and time; system actions are grey
  with mono IDs and timestamps. This puts Principles III and IV on screen.
- **Screens**: staff shell with a read-only tenant banner (no tenant switcher), claims list, claim
  workspace (case file · AI decision · evidence · decision trace), human review queue with the
  override/justification dialog, policy versions, security events (auditors), and the tenant-branded claimant portal (submit,
  access, status, supplement). Prototype screens without a requirement here (dashboard, AI
  operations, knowledge base, administration, partner, mobile, demo) are out of scope.

### Testing strategy

| Layer | What | How |
|-------|------|-----|
| Unit | Guardrail rules (every FR-026/027/028 condition and boundary, e.g., value = limit, one signal → not `Low`, request count 2 vs 3, returned-from-review), risk level derivation, duplicate window (89/90/91 days), claimant-text screen, evidence match rules (tolerance boundaries, seller suffixes), exclusion evidence map, prompt privacy (no customer identifiers in rendered prompts), coverage-window arithmetic, claim state machine, reference-ID mapping, redaction, injection detector, tool permission checks | xUnit v3, no I/O |
| Architecture | Dependency rules: Domain/Guardrails free of AI and infrastructure; only Gateway references `Anthropic`; Integrations only via ports | NetArchTest |
| Integration | RLS and query filters (including raw-SQL probes as another tenant), knowledge retrieval filters and version selection, blob isolation, job queue, API endpoints with test auth | Testcontainers (Postgres+pgvector, Azurite), WebApplicationFactory |
| Scenario (deterministic AI) | The FR-043 scenarios end to end (auto-approve, auto-reject, request info, high value, suspicious, override, same claim/different tenant, cross-tenant denial) plus policy-version, AI-failure, reviewer-return, request-limit, duplicate-window, single-signal, self-review, security-event, exclusion-grounding and matching-tolerance scenarios (quickstart S1–S23) | Replay model provider + recorded responses |
| Evaluation | Golden dataset metrics (R19) | `tests/Warranty.Evaluation` (`--replay` in CI, `--live` opt-in) |
| Frontend | Forms, review decision rules (justification required on override/reject; claimant message required on approve/reject and pre-filled only when matching the AI), trace rendering, claimant access flow; UI kit behaviour (status mappings, confidence meter semantics, dialog focus handling) | Vitest + RTL + MSW |
| Smoke | Whole AppHost boots and one claim completes | Aspire testing builder (manual / nightly) |

### PoC simplifications and future extension points

| PoC simplification | Extension point (already in the design) |
|--------------------|------------------------------------------|
| Shared DB + RLS for all tenants | `ITenantDataSourceResolver` → schema- or database-per-tenant |
| pgvector partitions in the same Postgres server | `IKnowledgeStore` → Qdrant / Azure AI Search / dedicated cluster |
| Case knowledge assembled from claim records, not vector-indexed | `ICaseKnowledgeProvider` → index technician reports, communications |
| Worker in the API process, Postgres job table | Move `ClaimJobWorker` to its own host; swap `IJobQueue` for a broker |
| Simulated CRM, ERP, service network, notifications, payment | Integration ports in `Warranty.Application/Integrations` → real adapters |
| One primary chat provider | `IModelProvider` → add `ChatClientModelProvider` (any `IChatClient`) and route per task |
| Local embedding model | `IEmbeddingGenerator` registration → hosted embeddings (re-index) |
| Risk as a capability inside the Decision step | `IRiskAssessor` → standalone Risk/Fraud agent |
| Regex PII redaction; photos not redacted | `IPiiRedactor` → NER-based redaction, image redaction |
| HEIC photos rejected | HEIC→JPEG normalizer in `UploadSanitizer` |
| Tenants/policies seeded from files | Tenant onboarding and policy authoring UI/API |
| Exact-hash reused-photo detection | Perceptual hashing |
| Model-reported confidence used as-is | Calibration layer fed by evaluation results |
| Fixed signal severities; fixed limits (2 automatic information requests, 90-day duplicate window) | Tenant settings for the limits; `IRiskAssessor` weights from configuration |
| Term-list claimant-text screen (`ClaimantTextScreen`) | Classifier-based disclosure detection behind the same interface |
| Operator view of tenant-less and cross-tenant security events via the database / telemetry only | Platform operator console with its own role and audit |
| Fixed exclusion ↔ photo damage-type map; `UNAUTHORIZED_REPAIR` always reviewed | Per-tenant evidence rules; tamper-detection evidence type |
| Disk-level encryption only; Azurite unencrypted | Managed storage encryption, column-level encryption for PII |
| AI provider retention under its standard commercial API terms (no training, R28); no zero-data-retention agreement | Zero-data-retention terms, or a regional/self-hosted provider behind `IModelProvider` |
| Uploads sanitized (magic bytes, photo metadata stripped, risky PDFs rejected — R11) but not malware-scanned | Malware scanning step in `UploadSanitizer` |
| Single region, single language (English) | Localization of claimant explanations, multi-region data residency |
| Tenant marker colours mapped from the display name in the SPA (`tenantTheme.ts`) | Branding fields in tenant settings, served by `/api/public/tenant` and `/api/me` |

### Local development setup (summary)

Prerequisites: .NET 10 SDK, Aspire CLI, Node.js 24 LTS, Podman 5 (machine running), an Anthropic
API key. `DOTNET_ASPIRE_CONTAINER_RUNTIME=podman`, then `aspire run` from the repo root starts
Postgres, Azurite, Keycloak, Ollama, the migration service (migrate → seed → index), the API and the
Vite dev server. Full steps and validation scenarios: [quickstart.md](./quickstart.md).

## Project Structure

### Documentation (this feature)

```text
specs/001-ai-claim-adjudication/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
├── ui-design.md         # WarrantyOS design system applied to the SPA (binding for src/web tasks)
├── design/              # Claude Design source: WarrantyOS.dc.html + support.js (reference only)
├── contracts/           # Phase 1 output (/speckit-plan command)
│   ├── rest-api.openapi.yaml
│   ├── ai-gateway.md
│   ├── rag.md
│   ├── agents-and-tools.md
│   └── schemas/
│       ├── intake-extraction.schema.json
│       ├── invoice-extraction.schema.json
│       ├── photo-analysis.schema.json
│       ├── policy-assessment.schema.json
│       └── decision-recommendation.schema.json
├── checklists/
│   └── requirements.md
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)

```text
Warranty.slnx
Directory.Build.props              # nullable, warnings-as-errors, analyzers, LangVersion
Directory.Packages.props           # central package management
src/
├── Warranty.AppHost/              # .NET Aspire: postgres(+pgvector), azurite, keycloak, ollama,
│                                  #   migration service, api, web; Podman runtime
├── Warranty.ServiceDefaults/      # OpenTelemetry, health checks, resilience, service discovery
├── Warranty.Api/
│   ├── Endpoints/                 # Public/ (claimant channel), Claims/, Review/, Trace/, Me/
│   ├── Tenancy/                   # TenantResolutionMiddleware, channel registry lookup
│   ├── Auth/                      # JWT bearer, role policies, claimant token issuer
│   └── Workers/                   # ClaimJobWorker (BackgroundService)
├── Warranty.Domain/
│   ├── Tenancy/  Catalog/  Policies/  Claims/  Adjudication/  Review/  Audit/
├── Warranty.Application/
│   ├── Abstractions/              # ITenantContext, IClock, ports: AI/, Knowledge/, Storage/, Integrations/
│   ├── Claims/                    # SubmitClaim, SupplementClaim, queries
│   ├── Review/                    # RecordReviewDecision, review queue
│   ├── Actions/                   # ActionExecutor (accepts ApprovedAction only)
│   └── Trace/                     # decision-trace read model
├── Warranty.Guardrails/
│   ├── Pipeline/                  # ordered checks, GuardrailEngine
│   ├── Rules/                     # coverage window, thresholds, conflicts, tenant rules
│   └── ApprovedAction.cs
├── Warranty.AI.Harness/
│   ├── Execution/                 # AdjudicationRunner, AdjudicationContext, checkpoints, retries
│   ├── Agents/                    # IntakeAgent, EvidenceAgent, PolicyAgent, DecisionAgent, Risk/
│   ├── Tools/                     # ITool, ToolRegistry, permission policy, tool implementations
│   ├── Context/                   # ContextBuilder (token budgets), ReferenceRegistry ([EV-n]/[POL-n])
│   └── Safety/                    # untrusted-content wrapping, injection detector
├── Warranty.AI.Gateway/
│   ├── Routing/  Prompts/  Redaction/  Usage/  RateLimiting/  Imaging/
│   ├── Providers/Anthropic/       # only project referencing the Anthropic SDK
│   ├── Providers/Embeddings/      # Ollama IEmbeddingGenerator
│   ├── Providers/Replay/          # recorded responses for tests/eval
│   └── PromptTemplates/           # *.v1.md, versioned, platform-owned
├── Warranty.Knowledge/
│   ├── Ingestion/                 # front-matter parsing, clause chunker, embedder
│   └── Retrieval/                 # filter-first retriever, namespace guard
├── Warranty.Infrastructure/
│   ├── Persistence/               # WarrantyDbContext, KnowledgeDbContext, migrations, RLS interceptor
│   ├── Storage/                   # BlobDocumentStore
│   ├── Jobs/                      # PostgresJobQueue
│   └── Audit/                     # DecisionTrailWriter (hash chain), SecurityEventWriter
├── Warranty.Integrations.Simulated/  # CRM, ERP serial registry, service network, notifications, payment
├── Warranty.MigrationService/     # migrate → roles/RLS → seed → index knowledge → exit
└── web/                           # React + TypeScript + Vite
    ├── src/app/                   # router, providers (query client, OIDC)
    ├── src/features/claimant/     # submit claim, access claim, status, supplement
    ├── src/features/claims/       # claim list, claim detail, evidence viewer
    ├── src/features/review/       # review queue, decision form
    ├── src/features/trace/        # decision trace timeline, AI call details
    ├── src/shared/api/            # generated OpenAPI types + client
    ├── src/shared/styles/         # tokens.css, base.css (WarrantyOS foundations)
    ├── src/shared/ui/             # component kit (ui-design.md §5)
    ├── src/shared/presentation/   # status/decision/actor → label + tone mappings, formatting
    └── tests/
tests/
├── Warranty.UnitTests/            # includes architecture (NetArchTest) tests
├── Warranty.IntegrationTests/     # Testcontainers, API, isolation suite, replay scenarios
├── Warranty.Evaluation/           # golden-dataset runner (separate from production)
└── fixtures/ai-recordings/        # recorded model responses per scenario
seed/
├── global/                        # terminology, generic fraud patterns, injection phrase list
├── tenants/aurora/                # tenant.json, products.json, customers.json, policies/*.md
├── tenants/borealis/
├── evidence/                      # synthetic invoices (PDF) and photos
└── golden/                        # golden claims + expected outcomes
infra/
└── keycloak/warranty-realm.json   # realm, clients, roles, synthetic staff users
```

**Structure Decision**: Web application layout with a .NET modular monolith under `src/` and the
SPA under `src/web/`. Each backend project maps to one architectural layer from the user's
direction, and the dependency rules (Domain ← Application ← Guardrails/Harness/Knowledge/
Infrastructure ← Api; Gateway implements an Application port; only Gateway references a vendor AI
SDK; Guardrails references no AI project) are enforced by architecture tests.

## Complexity Tracking

> Tensions with Principle VIII (PoC-First) that are deliberately accepted.

| Item | Why Needed | Simpler Alternative Rejected Because |
|------|------------|-------------------------------------|
| 10 backend projects instead of 2–3 | Each boundary enforces a constitutional guarantee (Guardrails isolated from AI → II; Gateway the sole SDK user → VI; Knowledge owning namespace enforcement → I/V) and is checked by architecture tests | Folder-only separation cannot be enforced at compile time; the PoC's purpose is to demonstrate these boundaries |
| PostgreSQL RLS in addition to EF query filters | Principle I requires isolation that survives a single coding mistake (raw SQL, `IgnoreQueryFilters`) | Query filters alone fail open on one bug |
| Keycloak instead of development-only JWTs | User requires an OAuth2/OIDC-compatible provider with RBAC; realm import keeps setup to one file | Dev JWTs don't exercise real OIDC flows or role mapping |
| Two model providers (Anthropic + local embeddings) | The primary chat provider has no embeddings endpoint; also demonstrates provider independence | A hosted embeddings API adds a second paid account and sends policy text off-machine |
| Hash-chained audit trail | Cheap (one column + trigger) and makes FR-039 tamper-evident | Plain append-only table cannot show tampering by a privileged user |
