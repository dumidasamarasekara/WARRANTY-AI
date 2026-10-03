# Data Model: AI-Powered Warranty Claim Adjudication PoC

**Feature**: `specs/001-ai-claim-adjudication` | **Date**: 2026-10-02 (updated 2026-10-03 for the spec clarifications) | **Plan**: [plan.md](./plan.md)

Conventions:

- IDs are UUID v7 unless stated. Timestamps are `timestamptz` (UTC). Money is `numeric(12,2)` plus
  an ISO-4217 currency code.
- **Every tenant-owned table has `tenant_id uuid not null`**, an EF Core global query filter, and a
  PostgreSQL RLS policy `USING (tenant_id = current_setting('app.tenant_id')::uuid)` with
  `FORCE ROW LEVEL SECURITY`. Tables marked *(platform)* are not tenant-owned.
- Composite foreign keys include `tenant_id` (e.g., `(tenant_id, claim_id)`) so a row can never
  reference another tenant's row, even through a bug.
- Logical data domains map to PostgreSQL schemas in database `warranty`; RAG data lives in the
  separate database `knowledge`; files live in blob storage.

## Overview

```text
tenancy.tenants 1─┬─* tenancy.tenant_channels
                  ├─1 tenancy.tenant_settings
                  ├─* catalog.products 1─* catalog.product_serials
                  ├─* crm.customers                         (simulated CRM)
                  ├─* policy.warranty_policies 1─* policy.policy_versions 1─* policy.policy_clauses
                  └─* claims.claims 1─┬─* claims.claim_evidence
                                      ├─* claims.claim_jobs
                                      ├─* adjudication.adjudication_runs 1─┬─1 intake_results
                                      │                                    ├─* evidence_findings
                                      │                                    ├─* retrieved_policy_refs
                                      │                                    ├─1 policy_assessments
                                      │                                    ├─1 risk_assessments
                                      │                                    ├─1 recommendations
                                      │                                    └─1 guardrail_evaluations
                                      ├─* review.review_decisions
                                      ├─* audit.decision_trail_entries
                                      ├─* aiops.model_calls / tool_calls / rag_queries
                                      └─* integration.repair_requests / notifications (simulated)
knowledge db: knowledge_documents 1─* knowledge_chunks (LIST-partitioned by namespace)
```

## Domain: tenancy

### `tenancy.tenants` *(platform)*

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| slug | text | unique, `^[a-z][a-z0-9-]{2,30}$`; used for knowledge namespace `tenant-{slug}` and blob container |
| display_name | text | required |
| status | enum `Active`, `Suspended` | only `Active` tenants are resolved |
| created_at | timestamptz | |

### `tenancy.tenant_channels` *(platform)*

Maps a claimant-channel hostname to a tenant (R9).

| Field | Type | Rules |
|-------|------|-------|
| hostname | text | PK, lower-case, exact match (e.g., `aurora.localhost`) |
| tenant_id | uuid | FK tenants |

### `tenancy.tenant_settings`

| Field | Type | Rules |
|-------|------|-------|
| tenant_id | uuid | PK/FK |
| currency | char(3) | e.g., `USD` |
| auto_approval_limit | numeric(12,2) | > 0; claim value **strictly above** → human review |
| min_confidence | int | 0–100; confidence **below** → human review |
| auto_approve_enabled | bool | default true |
| auto_reject_enabled | bool | default true (clarification Q2) |
| always_review_categories | text[] | product categories that always need a human decision |
| risk_high_threshold | int | default 60 (risk score 0–100); separates `Medium` from `High` only — any risk signal already makes risk at least `Medium` (R23) |
| version | int | optimistic concurrency |

## Domain: catalog (simulated ERP data)

### `catalog.products`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id | uuid | |
| model_code | text | unique per tenant |
| name | text | |
| category | text | e.g., `tablet`, `laptop`, `smart-home`, `major-appliance` |
| claim_value | numeric(12,2) | **fixed claim value used for the auto-approval limit check** (clarification Q4) |
| currency | char(3) | equals tenant currency |

### `catalog.product_serials`

| Field | Type | Rules |
|-------|------|-------|
| tenant_id | uuid | PK part |
| serial_number | text | PK part; upper-case, trimmed |
| product_id | uuid | FK `(tenant_id, product_id)` |
| manufactured_on | date | optional |

A claim whose product/serial pair is not found here gets risk signal `PRODUCT_NOT_IN_CATALOG`, has
no claim value, and goes to human review (FR-003).

## Domain: crm (simulated CRM)

### `crm.customers`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id | uuid | |
| full_name | text | PII — never sent to models (replaced by `[CUSTOMER]`) |
| email | text | PII; normalized lower-case |
| phone | text | PII; E.164 normalized |
| address_line, city, postal_code | text | PII |
| country | char(2) | ISO-3166 |
| region | enum `NA`, `EU` | derived from country |

Customers are matched by (tenant, normalized email) at submission or created.

## Domain: policy

### `policy.warranty_policies`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id | uuid | |
| code | text | unique per tenant (e.g., `AUR-WP`) |
| title | text | |

### `policy.policy_versions`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id | uuid | |
| policy_id | uuid | FK |
| version | int | unique per policy |
| effective_from | date | required |
| effective_to | date | nullable (open-ended); versions of one policy must not overlap |
| regions | text[] | `{NA,EU}` or subset |
| product_categories | text[] | empty = all categories |
| terms | jsonb | **structured coverage terms** used by guardrails (below) |
| source_blob_path | text | original Markdown in `knowledge-sources` |
| checksum | text | SHA-256 of source |

`terms` structure (validated on seed):

```json
{
  "standardCoverageMonths": { "NA": 12, "EU": 24 },
  "componentCoverageMonths": { "battery": 6 },
  "accidentalDamage": { "covered": false, "windowMonths": 0, "maxIncidents": 0 },
  "exclusions": ["ACCIDENTAL_DAMAGE", "LIQUID_DAMAGE", "COSMETIC_DAMAGE", "UNAUTHORIZED_REPAIR"]
}
```

### `policy.policy_clauses`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id | uuid | |
| policy_version_id | uuid | FK |
| clause_key | text | stable key, unique per version (e.g., `AUR-WP-3.2`) |
| clause_type | enum `Coverage`, `Period`, `Exclusion`, `ServiceRule`, `Definition` | |
| title | text | |
| text | text | clause wording (source of truth; also indexed in knowledge DB) |

**Version selection rule** (clarification Q1): the applicable version is the one with
`effective_from ≤ purchase_date ≤ coalesce(effective_to, ∞)` for the claim's region and product
category. Zero matches → `NO_APPLICABLE_POLICY` (human review, never auto-reject, FR-014). More
than one match → `AMBIGUOUS_POLICY_VERSION` (human review).

**Coverage window rule** (deterministic, guardrails): `coverage_end = purchase_date +
months(component-specific or standard months for region)`; covered iff `claim_date ≤ coverage_end`.
Accidental damage additionally requires `accidentalDamage.covered` and `claim_date ≤ purchase_date +
windowMonths` and fewer prior approved accidental claims for the serial than `maxIncidents`.

## Domain: claims

### `claims.claims`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id | uuid | |
| reference | text | unique per tenant; 10-char Crockford base32, random (not enumerable) |
| channel | enum `ClaimantPortal`, `AgentPortal` | |
| submitted_by | text | staff `sub`, or `claimant` |
| customer_id | uuid | FK `(tenant_id, customer_id)` |
| contact_email / contact_phone | text | as given at submission; used for claimant access (FR-037a), constant-time comparison after normalization |
| product_model_code | text | as submitted |
| product_id | uuid | nullable — set when found in catalog |
| serial_number | text | required, normalized |
| purchase_date | date | required; not in future; ≤ claim_date — enforced at submission (400); the intake `PURCHASE_DATE_*` checks re-verify it as a backstop |
| purchase_place | text | required |
| purchase_price | numeric(12,2) | required, > 0 (cross-checked against invoice; not the claim value) |
| region | enum `NA`, `EU` | from purchase place country, else customer address |
| problem_description | text | required, 20–4,000 chars; untrusted content |
| claim_date | date | = submission date (UTC) |
| status | enum (see state machine) | |
| final_outcome | enum `Approved`, `Rejected` | null until final |
| final_decided_by | enum `System`, `Reviewer` | null until final |
| final_explanation | text | claimant-facing explanation, no risk signals: the AI's `claimant_explanation` for automatic decisions, the reviewer's `claimant_explanation` for reviewer decisions (FR-036, FR-037) |
| finalized_at | timestamptz | null until `Approved`/`Rejected`; used by the 90-day duplicate window (R25) |
| requested_items | jsonb | items requested from the submitter while `PendingInformation` |
| current_round | int | starts at 1; +1 per supplement |
| auto_info_request_count | int | default 0; +1 when a guardrail-issued `RequestInformation` is executed; at 2, a further need for information escalates (FR-010, R24) |
| reviewer_info_requested | bool | default false; set when a reviewer's `RequestInformation` is executed; never cleared — later rounds always end in `HumanReview` (FR-034, R24) |
| row_version | xid | optimistic concurrency (review decisions use `If-Match`) |
| created_at / updated_at | timestamptz | |

### Claim state machine

```text
                 submit
                   │
                   ▼
              Submitted ──(job picked up)──► UnderEvaluation
                                                │
          ┌───────────────┬─────────────────────┼───────────────────────┐
          ▼               ▼                     ▼                       ▼
  PendingInformation  UnderReview            Approved               Rejected
  (missing items /    (escalated / AI       (AutoApprove)          (AutoReject)
   AI or reviewer      failure)                 ▲                       ▲
   request)               │ reviewer           │                       │
     │ supplement         ├──── approve ───────┘                       │
     └─► UnderEvaluation  ├──── reject ────────────────────────────────┘
        (round + 1)       └──── request info ──► PendingInformation
```

Rules: `Approved` and `Rejected` are terminal; supplements are refused on terminal claims; only the
`ActionExecutor` (holding an `ApprovedAction` or a recorded reviewer decision) can move a claim to
`Approved`, `Rejected` or `PendingInformation`. Claimant-visible names: Submitted, Under Evaluation,
Pending Information, Under Review, Approved, Rejected (FR-037).

Loop limits (R24): a supplement after a **reviewer's** information request still goes
`UnderEvaluation` (full re-evaluation), but its run always ends in `UnderReview` with reason
`RETURNED_AFTER_REVIEWER_REQUEST`. An **automatic** information request is allowed only while
`auto_info_request_count < 2`; otherwise the run ends in `UnderReview` with reason
`INFO_INCOMPLETE_AFTER_2_REQUESTS`. The `PendingInformation` → `UnderEvaluation` edge is therefore
taken at most twice without a reviewer involved.

### `claims.claim_evidence`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id | uuid | |
| claim_id | uuid | FK `(tenant_id, claim_id)` |
| round | int | submission round that added it |
| kind | enum `Invoice`, `Photo`, `Other` | ≥1 `Invoice` and ≥1 `Photo` required per claim |
| file_name | text | sanitized |
| content_type | text | `image/jpeg`, `image/png`, `image/webp`, `application/pdf` (HEIC rejected in the PoC) |
| size_bytes | bigint | ≤ 15 MB |
| sha256 | char(64) | for reused-evidence detection within tenant |
| blob_path | text | `tenant-{slug}/claims/{claimId}/{round}/{evidenceId}{ext}` |
| uploaded_at | timestamptz | |

### `claims.claim_jobs`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id | uuid | the only tenant source for the worker (R9) |
| claim_id | uuid | |
| round | int | unique `(claim_id, round)` |
| status | enum `Queued`, `Running`, `Done`, `Failed` | |
| attempts | int | max 3 (infrastructure failures only) |
| available_at / locked_until | timestamptz | polled only through `claims.dequeue_claim_job()` (`SECURITY DEFINER`, `FOR UPDATE SKIP LOCKED`, returns job headers only) |
| correlation_id | text | propagated to the run and traces |

## Domain: adjudication

### `adjudication.adjudication_runs`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id, claim_id | uuid | |
| round | int | unique `(claim_id, round)` |
| correlation_id | text | |
| status | enum `Running`, `Completed`, `Failed` | |
| current_step | enum `Intake`, `Evidence`, `Policy`, `Risk`, `Decision`, `Guardrails`, `Action`, `Done` | execution state checkpoint |
| disposition | enum `AutoApprove`, `AutoReject`, `RequestInformation`, `HumanReview` | null until guardrails ran |
| failure_reason | text | for FR-031 |
| reference_map | jsonb | harness-issued IDs: `{"EV-1": "<evidenceId>", "POL-1": "<chunkId>", ...}` |
| started_at / completed_at | timestamptz | |

### `adjudication.intake_results`

`run_id` (PK/FK), `tenant_id`, `validation` jsonb — array of `{check, passed, detail}` for each
FR-009 check (`REQUIRED_FIELDS`, `PHOTO_PRESENT`, `INVOICE_PRESENT`, `INVOICE_LEGIBLE`,
`FILE_TYPES`, `PURCHASE_DATE_NOT_FUTURE`, `PURCHASE_DATE_BEFORE_CLAIM`, `REGION_DETERMINED`),
`extraction` jsonb (per [intake-extraction.schema.json](./contracts/schemas/intake-extraction.schema.json)),
`missing_items` jsonb.

### `adjudication.evidence_findings`

`id`, `tenant_id`, `run_id`, `evidence_id`, `kind` (`InvoiceExtraction`, `PhotoAnalysis`),
`result` jsonb (per invoice/photo schema; photo results include `confidence`), `consistency` jsonb — deterministic cross-checks
`{field, claimValue, evidenceValue, match}` for product, serial, purchase date, price, seller
(FR-016).

### `adjudication.retrieved_policy_refs`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id, run_id | uuid | |
| ref_id | text | `POL-n` issued for this run |
| knowledge_chunk_id | uuid | chunk in knowledge DB |
| policy_version_id | uuid | |
| clause_key | text | |
| document_title, version, effective_from, effective_to | | denormalized for display and audit (FR-013) |
| score | real | similarity within the filtered set |
| cited | bool | true if the recommendation cites it |

### `adjudication.policy_assessments`

`run_id` (PK/FK), `tenant_id`, `version_outcome` enum `Ok`/`NoApplicablePolicy`/`AmbiguousPolicyVersion`,
`assessment` jsonb (per [policy-assessment.schema.json](./contracts/schemas/policy-assessment.schema.json),
incl. `confidence`), `model`, `prompt_version`.

### `adjudication.risk_assessments`

`run_id` (PK/FK), `tenant_id`, `stage` enum `Intake`/`Full` (`Intake` = signals available before
evidence analysis, used when the intake short-circuit applies), `score` int 0–100, `level` enum
`Low`/`Medium`/`High`,
`signals` jsonb — array of `{code, source: Deterministic|AI, severity, detail, evidenceRefs[]}`.

Signal codes (FR-017): `SOURCE_INCONSISTENCY`, `PRODUCT_NOT_IN_CATALOG`, `SERIAL_MISMATCH_PHOTO`,
`DUPLICATE_SERIAL_CLAIM`, `EVIDENCE_REUSED`, `DAMAGE_INCONSISTENT_WITH_DESCRIPTION`,
`PURCHASE_DATE_ANOMALY`, `MANIPULATION_ATTEMPT`, `OTHER` (AI only).

Level rule (FR-017, R23), computed over the union of deterministic and AI-reported signals:

| Signals | Score | Level |
|---------|-------|-------|
| none | 0 | `Low` — the only way to reach `Low` |
| ≥ 1 | `min(100, Σ weight)`; weight `Low` 10 · `Medium` 25 · `High` 40 | `High` if score ≥ `risk_high_threshold`, else `Medium` |

Fixed severities: `MANIPULATION_ATTEMPT`, `EVIDENCE_REUSED`, `SERIAL_MISMATCH_PHOTO` → `High`;
`DUPLICATE_SERIAL_CLAIM`, `PRODUCT_NOT_IN_CATALOG`, `SOURCE_INCONSISTENCY`,
`PURCHASE_DATE_ANOMALY` → `Medium`; AI-only signals → `Medium`. A code raised by both sources is
counted once, with the deterministic severity. Photos that do not show the product or the damage
are missing information (`PHOTO_OF_DAMAGE`, `PHOTO_OF_SERIAL_LABEL`), not a signal.

`DUPLICATE_SERIAL_CLAIM` (R25): another claim of the tenant with the same normalized serial that
is not final, or whose `finalized_at` is within 90 days before this claim's `claim_date`. Earlier
rounds of the same claim never count.

### `adjudication.recommendations`

| Field | Type | Rules |
|-------|------|-------|
| run_id | uuid | PK/FK |
| tenant_id | uuid | |
| raw_output | jsonb | model output after redaction, as received |
| is_valid | bool | schema + reference validation result |
| validation_errors | jsonb | |
| decision | enum `APPROVE`, `REJECT`, `REQUEST_MORE_INFORMATION`, `HUMAN_REVIEW` | |
| coverage | enum `COVERED`, `NOT_COVERED`, `UNDETERMINED` | |
| confidence | int | 0–100 |
| reasoning_summary | text | staff-facing |
| claimant_explanation | text | claimant-facing; no risk/fraud details |
| evidence_refs / policy_refs / missing_information | jsonb | resolved from `EV-n` / `POL-n` |
| manipulation_detected | bool | |
| model, prompt_id, prompt_version | text | |

The recommendation is **never modified** after creation (FR-036).

### `adjudication.guardrail_evaluations`

`run_id` (PK/FK), `tenant_id`, `checks` jsonb — ordered array of
`{code, stage, passed, expected, actual, message}`, `disposition`, `reasons` jsonb,
`approved_action` jsonb (action type + parameters, when issued), `evaluated_at`.

Check codes: `SCHEMA_VALID`, `REFERENCES_VALID`, `REQUIRED_INFO_COMPLETE`, `PRODUCT_IN_CATALOG`,
`POLICY_APPLICABLE`, `COVERAGE_WINDOW_AGREES`, `CLAIM_VALUE_WITHIN_LIMIT`, `CONFIDENCE_AT_OR_ABOVE_MIN`,
`RISK_LOW`, `NO_CONFLICTS`, `NO_MANIPULATION`, `CATEGORY_NOT_ALWAYS_REVIEW`, `GROUNDED_IN_CLAUSE`,
`AUTO_DECISION_ENABLED`, `ACTOR_AUTHORIZED`, `NOT_RETURNED_FROM_REVIEW`,
`AUTO_INFO_REQUESTS_WITHIN_LIMIT`, `CLAIMANT_TEXT_SAFE`.

`RISK_LOW` passes only when no risk signal is present (R23). `NOT_RETURNED_FROM_REVIEW` fails when
`claims.reviewer_info_requested`; `AUTO_INFO_REQUESTS_WITHIN_LIMIT` fails when a
`RequestInformation` disposition would be issued with `auto_info_request_count ≥ 2` (R24);
`CLAIMANT_TEXT_SAFE` fails when the AI's claimant explanation contains disclosure terms (R25).

Escalation reason codes (`reasons`, shown to reviewers with a readable label): `VALUE_ABOVE_LIMIT`,
`CONFIDENCE_BELOW_MIN`, `ALWAYS_REVIEW_CATEGORY`, `RISK_MEDIUM`, `RISK_HIGH`, `EVIDENCE_CONFLICT`,
`AI_DETERMINISTIC_DISAGREEMENT`, `INVALID_RECOMMENDATION`, `AI_UNAVAILABLE`, `AI_RECOMMENDS_REVIEW`,
`NO_APPLICABLE_POLICY`, `AMBIGUOUS_POLICY`, `PRODUCT_NOT_IN_CATALOG`,
`RETURNED_AFTER_REVIEWER_REQUEST` ("returned after reviewer information request"),
`INFO_INCOMPLETE_AFTER_2_REQUESTS` ("information still incomplete after 2 requests"),
`UNSAFE_CLAIMANT_TEXT`. For the `claims-agent` role, the risk-related reasons (`RISK_MEDIUM`,
`RISK_HIGH`, `EVIDENCE_CONFLICT`, `AI_DETERMINISTIC_DISAGREEMENT`, `UNSAFE_CLAIMANT_TEXT`) are shown
only as "Additional checks required", and check details are not returned (FR-005).

## Domain: review

### `review.review_decisions`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id, claim_id, run_id | uuid | decision applies to the latest run |
| reviewer_sub / reviewer_name | text | from token |
| decision | enum `Approve`, `Reject`, `RequestInformation` | |
| justification | text | internal, staff-only; **required** when `overrides_ai` or `decision = Reject` (FR-035); 10–2,000 chars; never shown to claimants |
| claimant_explanation | text | **required** for `Approve`/`Reject`, absent for `RequestInformation`; 20–1,500 chars; must pass `ClaimantTextScreen` (no risk/fraud terms or reference IDs); becomes `claims.final_explanation` (FR-036, FR-037, R25) |
| requested_items | jsonb | required non-empty when `RequestInformation` |
| overrides_ai | bool | computed: true only when the latest run has a **valid** recommendation of `APPROVE` or `REJECT` and the decision differs (`APPROVE`↔`Approve`, `REJECT`↔`Reject`); false when the recommendation is missing/invalid (AI failure) or is `HUMAN_REVIEW` / `REQUEST_MORE_INFORMATION` (FR-035) |
| decided_at | timestamptz | |

Allowed only when claim status is `UnderReview`; second concurrent decision → 409 (row version).
Executing a `RequestInformation` decision sets `claims.reviewer_info_requested = true` (R24).

## Domain: audit

### `audit.decision_trail_entries` (append-only)

| Field | Type | Rules |
|-------|------|-------|
| id | bigserial | PK |
| tenant_id, claim_id | uuid | |
| seq | int | per claim, gap-free |
| occurred_at | timestamptz | |
| step | enum `ClaimSubmitted`, `TenantResolved`, `EvidenceStored`, `IntakeValidated`, `ClaimExtracted`, `CustomerVerified`, `ProductIdentified`, `PolicyRetrieved`, `EvidenceAnalyzed`, `CoverageAssessed`, `RiskEvaluated`, `AiRecommended`, `GuardrailsEvaluated`, `AutoApproved`, `AutoRejected`, `InformationRequested`, `EscalatedToReview`, `ReviewerDecided`, `SupplementReceived`, `ActionExecuted`, `AiStepFailed`, `Correction` | |
| actor | text | `system`, agent name, or staff `sub` |
| summary | text | one-line, human-readable (rendered in the trace UI) |
| payload | jsonb | step details / references to rows above |
| correlation_id | text | |
| prev_hash / hash | char(64) | `hash = sha256(prev_hash ‖ canonical_json(entry))` |

App role has `INSERT, SELECT` only; trigger rejects `UPDATE`/`DELETE`.

### `audit.security_events`

`id`, `tenant_id` (nullable when tenant could not be resolved), `occurred_at`, `kind`
(`CROSS_TENANT_ACCESS_DENIED`, `CLAIMANT_ACCESS_FAILED`, `RETRIEVAL_SCOPE_VIOLATION`,
`TOOL_SCOPE_VIOLATION`, `UNKNOWN_CHANNEL`), `actor`, `target`, `source_ip`, `details` jsonb.
Append-only like the trail.

## Domain: aiops (AI execution records)

| Table | Key fields |
|-------|-----------|
| `aiops.model_calls` | id, tenant_id, claim_id, run_id, agent, route, provider, model, prompt_id, prompt_version, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, latency_ms, estimated_cost, stop_reason, status (`Ok`, `Invalid`, `Refused`, `Error`, `Timeout`), attempt, error, correlation_id, started_at |
| `aiops.tool_calls` | id, tenant_id, run_id, agent, tool, arguments (redacted jsonb), allowed (bool), denial_reason, result_summary, latency_ms, status, started_at |
| `aiops.rag_queries` | id, tenant_id, run_id, agent, namespaces text[], filters jsonb, query_text (redacted), top_k, results jsonb (`[{chunkId, clauseKey, score}]`), latency_ms, started_at |

## Domain: integration (simulated)

| Table | Key fields |
|-------|-----------|
| `integration.service_centers` | id, tenant_id, region, name, capabilities text[] |
| `integration.repair_requests` | id, tenant_id, claim_id, service_center_id, status (`Created`), created_at — written only by `ActionExecutor` |
| `integration.notifications` | id, tenant_id, claim_id, channel (`Email`), template, created_at — simulated outbox, nothing is sent |

## Knowledge database (`knowledge`)

### `knowledge_documents`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| namespace | text | `global` or `tenant-{slug}` |
| tenant_id | uuid | null only for `global` |
| document_type | enum `WarrantyPolicy`, `ProductManual`, `ProductSpec`, `RepairRule`, `ReplacementRule`, `PricingRule`, `RegionalPolicy`, `ServiceLevelRule`, `Terminology`, `FraudPattern`, `Procedure` | |
| source_ref | text | e.g., `policy_version:{id}` |
| title, version | text, int | |
| product_category, product_model | text | nullable = all |
| regions | text[] | empty = all |
| effective_from / effective_to | date | |
| classification | enum `Public`, `Internal`, `Confidential` | |
| allowed_roles | text[] | e.g., `{adjudication-service,claims-reviewer,auditor}` |
| embedding_model, embedding_dim | text, int | |
| checksum | text | re-index only on change |

### `knowledge_chunks` — `PARTITION BY LIST (namespace)`

Fields: `id`, `namespace`, `tenant_id`, `document_id`, `chunk_index`, `clause_key`, `section_title`,
`text`, `embedding vector(768)`, and denormalized filter columns (`document_type`,
`product_category`, `product_model`, `regions`, `effective_from`, `effective_to`, `classification`,
`allowed_roles`). One partition per namespace with an HNSW cosine index. RLS:
`USING (namespace = 'global' OR namespace = current_setting('app.kb_namespace'))`.

## Blob storage layout

| Container | Content |
|-----------|---------|
| `tenant-{slug}` | `claims/{claimId}/{round}/{evidenceId}{ext}` |
| `knowledge-sources` | `global/...`, `tenant-{slug}/policies/{code}-v{n}.md` |

## Identity (Keycloak, not stored in app DB)

Token claims used by the API: `sub`, `name`, `tenant_id` (user attribute), realm roles
`claims-agent`, `claims-reviewer`, `auditor`. Each staff user has exactly one `tenant_id`
(clarification Q3). Claimant tokens are issued by the API: `sub = claimant:{claimId}`,
`tenant_id`, `claim_id`, `scope = claimant`, 30-minute expiry.

## Seed data (synthetic)

### Tenant A — `aurora` ("Aurora Electronics", fictional consumer electronics)

- Channel: `aurora.localhost`. Settings: USD, auto-approval limit **500**, min confidence **85**,
  always-review categories: none.
- Products: `AUR-TAB10` tablet (claim value 450), `AUR-PHN6` phone (claim value 480),
  `AUR-BOOK15` laptop (claim value 1,400).
- Policy `AUR-WP` "Aurora Limited Warranty":
  - **v1** effective 2025-01-01 → 2026-06-30: manufacturing defects 12 months (NA) / 24 months
    (EU); battery 6 months; excludes accidental, liquid, cosmetic damage and unauthorized repair.
  - **v2** effective 2026-07-01 → open: as v1 but battery 12 months.
- Historical claims for the duplicate-window scenario (quickstart S18), with dates computed
  relative to the seeding day so the window holds whenever the PoC is run: one `AUR-TAB10` serial
  with a claim `Approved` 30 days ago, another `AUR-TAB10` serial with a claim `Approved` 120 days
  ago (each with a minimal decision trail marked as seeded history).

### Tenant B — `borealis` ("Borealis Devices", fictional electronics & appliances)

- Channel: `borealis.localhost`. Settings: USD, auto-approval limit **1,000**, min confidence
  **80**, always-review categories: `major-appliance`.
- Products: `BOR-SLATE11` tablet (claim value 480), `BOR-HUB2` smart-home hub (claim value 220),
  `BOR-OVEN60` smart oven, category `major-appliance` (claim value 1,650).
- Policy `BOR-WP` "Borealis Care Warranty" **v1** effective 2024-01-01 → open: manufacturing
  defects 24 months (all regions); battery 12 months; one accidental-damage incident covered within
  the first 12 months; excludes liquid damage and unauthorized repair.

### Global namespace

Warranty terminology, generic repair concepts, generic fraud patterns, prompt-injection phrase
list, general operating procedures.

### Staff users (synthetic, Keycloak realm)

`agent.aurora`, `reviewer.aurora`, `auditor.aurora`, `agent.borealis`, `reviewer.borealis`,
`auditor.borealis` — development-only passwords in the realm file.
