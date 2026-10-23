# Research: Policy-Driven Remedies and Repair-Centre Hand-over

**Feature**: `specs/002-remedy-repair-handover` | **Date**: 2026-10-14 | **Plan**: [plan.md](./plan.md)

Decisions are numbered R1–R14 in this feature (references to feature 001's research use the form
"001 R13"). Every decision was checked against the code on `master` at `ad2881e`.

## R1 — Remedy rules are structured policy terms, not clause text

- **Decision**: Each policy version gets an optional `remedies` block next to the existing
  `terms` block in the policy's Markdown front-matter (`seed/tenants/*/policies/*.md`), stored as
  a `remedy_rules jsonb` column on `policy.policy_versions` and validated on seed against
  [contracts/remedy-rules.schema.json](./contracts/remedy-rules.schema.json). A version without
  the block gives no remedy (FR-012, manual review).
- **Rationale**: Feature 001 already decides coverage from structured `terms`, never from clause
  text (`CoverageTerms`, guardrails). Remedies follow the same rule, so the evaluator stays
  deterministic and AI-free (constitution II). Clause text, chunks and retrieval stay unchanged.
- **Alternatives considered**: New remedy clauses retrieved by RAG and read by the Decision Agent
  (rejected: the AI would effectively choose the remedy, against FR-014); a separate per-tenant
  remedy configuration outside the policy (rejected: FR-007 requires versioning with the policy).

## R2 — One deterministic remedy evaluator in `Warranty.Guardrails`

- **Decision**: `Warranty.Guardrails/Remedies/RemedyPolicyEvaluator` is a pure function:
  `(RemedyRules?, RemedyScenario, failedAttempts, estimate?, claimValue) → RemedyEvaluation`
  (allowed remedies, the single remedy when there is exactly one, `NeedsReviewer`, reason code,
  table row). Scenarios: `CoveredDefect`, `ProtectionPlanDamage`, `RepairNotEconomical`,
  `RepairFailed`. Below the attempt limit, `RepairFailed` returns `ContinueRepair`.
- **Rationale**: The guardrails project is the existing home of deterministic business rules
  with no AI reference (architecture tests). Every company runs the same code (FR-008); only the
  rules differ.
- **Alternatives considered**: Logic inside the `Repair` aggregate (rejected: mixes policy rules
  with state transitions); a rules engine library (rejected: four rows do not justify it,
  constitution VIII).

## R3 — The remedy is set inside the approval transaction

- **Decision**: `ActionExecutor` evaluates the remedy right after `claim.FinalizeApproved(...)`,
  for automatic approvals (`ActionKind.FinalizeApproved`) and reviewer approvals alike, and writes
  a `ClaimRemedy` in the same transaction. One allowed remedy → set (`DecidedBy = Policy`).
  Otherwise → `AwaitingDecision`, and the claim appears in the review queue as a remedy item. The
  claim's status and final outcome are never held back (FR-011, FR-016).
- **Rationale**: Keeps the outcome and its remedy consistent, reuses the executor's tenant and
  run checks, and keeps 001's outcomes unchanged (SC-009).
- **Alternatives considered**: A separate background job after approval (rejected: a window in
  which an approved claim has no remedy and no queue item); routing the whole claim to human
  review when the remedy is a choice (rejected: changes 001 outcomes).

## R4 — Reviewer remedy decisions go through `ActionExecutor`

- **Decision**: New `RecordRemedyDecision` use case (reviewer role, `If-Match` on the remedy) and
  `IActionExecutor.ExecuteRemedyDecisionAsync`. The executor re-runs the evaluator and refuses a
  remedy outside `AllowedRemedies` (FR-002), refuses self-review (`SELF_REVIEW_REFUSED`, as 001
  R29), requires a reason for every change after the first remedy (FR-015). A reviewer approving
  an escalated claim chooses the remedy in the same request (`ReviewDecisionRequest.remedy`).
- **Rationale**: A remedy drives a refund or a replacement, which are consequential; the
  executor is the single place where consequential effects happen (001 R13).
- **Alternatives considered**: Letting the endpoint write the remedy directly (rejected: two
  paths to the same effect, and the allowed-list check could be bypassed).

## R5 — Repair status is a domain state machine driven by staff commands

- **Decision**: New aggregate `Warranty.Domain/Fulfillment/Repair` owns status, the active
  hand-over, attempts and the failed-attempt count, and enforces the FR-024 transition table.
  Staff commands live in `Warranty.Application/Fulfillment/RecordRepairEvent` (one command, one
  event type per action). Optimistic concurrency: `If-Match` on the repair's row version (as
  001's `ClaimETag`); a stale version is `412`, a lost race on save is `409` (FR-028).
- **Rationale**: Repair updates are facts reported by a person, not AI proposals. They need
  validation and audit, not a guardrail decision. No AI tool can call them; the harness tool
  catalog is unchanged (constitution II).
- **Alternatives considered**: Issuing an `ApprovedAction` for each staff update (rejected: the
  guardrails would rubber-stamp human input without adding a check).

## R6 — History: structured append-only events plus the decision trail

- **Decision**: Every staff update writes one row to `audit.repair_events` (append-only, enforced
  by the existing `audit.*` trigger and grants) and one decision trail entry with new steps
  (`RemedyRecommended`, `RemedyDecided`, `RepairCentreAssigned`, `RepairHandedOver`,
  `RepairStatusUpdated`, `RepairAttemptEnded`, `RemedyCompleted`). Corrections append a
  `Correction` event that references the corrected event (FR-027).
- **Rationale**: The trail gives the readable, hash-chained history (001 FR-038/FR-039); the
  structured events give reported time, recorded time, source and the corrected-event link.
- **Alternatives considered**: Trail only (rejected: the trail payload is free JSON and hard to
  query for "current active hand-over"); mutable rows only (rejected: FR-042).

## R7 — Exactly one ready notification, enforced by the database

- **Decision**: `integration.notifications` gets a nullable `dedupe_key` with a unique index on
  `(tenant_id, dedupe_key)`. The ready notification uses `repair-ready:{attemptId}`, template
  `RepairReadyForCollection`, queued in the same transaction as the status change. A duplicate
  insert is treated as "already queued".
- **Rationale**: Retries, double clicks and concurrent staff cannot produce two rows (FR-038,
  SC-004), independent of application logic.
- **Alternatives considered**: An application-level "already notified" flag (rejected: races).

## R8 — Repair centres extend the existing service centres

- **Decision**: `integration.service_centers` gains `address`, `opening_hours`, `contact`,
  `is_active` and `priority`; seed files are extended and each region gets at least two active
  centres (FR-045). Suggestion rule: active, same region, product category in `capabilities`,
  highest priority, then name (FR-018). For claims approved with remedy Repair, the executor
  creates a `Repair` with the suggested centre instead of an `integration.repair_requests` row.
  `IRepairRequestService` and the `create_repair_request` tool name stay for history; rows of
  claims approved earlier are kept and shown as before.
- **Rationale**: FR-017 forbids a second list of locations; the selection rule is the existing
  `SimulatedServiceNetwork` rule with priority and the active flag added.
- **Alternatives considered**: A new `repair_centres` table (rejected by FR-017).
- **Impact on 001 tests**: tests that assert an `integration.repair_requests` row or the
  "Simulated repair request created" trail text after an approval are updated to assert the
  remedy and repair records. Outcome assertions do not change.

## R9 — Protection plan: intake field, evidence kind, guardrail escalation, reviewer confirmation

- **Decision**: The claim form gets `hasProtectionPlan` and a new evidence kind
  `ProtectionPlanDocument`. Intake validation requests the document when the flag is set and no
  document is attached (new requested item `PROTECTION_PLAN_DOCUMENT`, FR-037). A new guardrail
  escalation reason `PROTECTION_PLAN_TO_CHECK` sends a claim to human review when the flag is set,
  the claim's damage is accidental (an `ACCIDENTAL_DAMAGE` exclusion, or accidental damage beyond
  the version's allowance), and the version's `remedies.protectionPlan.liftsAccidentalDamageExclusion`
  is true. `ReviewDecisionRequest` gets `protectionPlanConfirmed`; an approval with a confirmed
  plan uses the `ProtectionPlanDamage` scenario. When the version does not let a plan lift the
  exclusion, the flag changes nothing (FR-006).
- **Rationale**: Plan validity is a document check the PoC cannot automate safely; a reviewer
  decides (constitution IV). The AI prompts and output schemas do not change, so replay
  recordings stay valid.
- **Alternatives considered**: Asking the Decision Agent to read the plan (rejected: the AI
  would decide coverage on a document type it was not evaluated on); a plan registry lookup
  (rejected: out of scope).

## R10 — Refund hand-off through a port

- **Decision**: New port `IRefundService.RequestRefundAsync(claimId, amount)` in
  `Application/Abstractions/Integrations`, called only by the executor when the remedy becomes
  Refund. Completion is recorded by staff with the refund reference (`RemedyCompletion`,
  FR-035). The PoC adapter in `Warranty.Integrations.Simulated` replaces the unused no-op
  `PaymentStub` and records a simulated refund request; no money moves.
- **Rationale**: The team states a refund option exists in the application. On `master` the only
  refund-related code is `PaymentStub`, which records nothing. The port lets the first
  implementation task bind the real refund option without changing the remedy logic.
- **Open item**: The task that implements the adapter must confirm where the existing refund
  option lives and bind to it; if it is outside this repository, the simulated adapter stays.
- **Alternatives considered**: Calling the refund option directly from the endpoint (rejected:
  bypasses the executor, constitution II).

## R11 — One canonical REST contract

- **Decision**: [contracts/rest-api.delta.openapi.yaml](./contracts/rest-api.delta.openapi.yaml)
  lists only the new and changed paths and schemas. The first API task merges it into
  `specs/001-ai-claim-adjudication/contracts/rest-api.openapi.yaml`, which stays the single
  contract the SPA generates its types from (`npm --prefix src/web run gen:api`).
- **Rationale**: The SPA's `gen:api` script and contract tests read the 001 file; two full
  contracts would drift.
- **Alternatives considered**: A full copy of the contract in 002 (rejected: drift).

## R12 — Seed data: rules on the existing versions, clause text unchanged

- **Decision**: The `remedies` blocks are added to the existing seeded versions `AUR-WP` v1 and
  v2 and `BOR-WP` v1 (FR-044). No new versions are created. Clause text and clause keys are not
  changed.
- **Rationale**: A new version overlapping the old ones is rejected by `PolicyVersion.OverlapsInTime`
  and would make version selection ambiguous (001 edge cases). Seeded versions are demonstration
  data. Unchanged clause text keeps retrieval results and `[POL-n]` numbering stable, so the
  recorded replay responses in `tests/fixtures/ai-recordings/` stay valid (replay matches by
  scenario and agent, not by prompt text). The checksum of the source changes, so the knowledge
  index re-ingests identical chunks.
- **Alternatives considered**: New v3/v2 versions with a future effective date (rejected: only
  products bought after that date would get remedy rules, which the demo cannot show).

## R13 — Scenario of an approved claim

- **Decision**: The initial scenario is `ProtectionPlanDamage` when the approving reviewer
  confirmed a protection plan, otherwise `CoveredDefect` (including Borealis's included
  accidental-damage incident, which is coverage). `RepairNotEconomical` and `RepairFailed` are
  evaluated only from repair events.
- **Rationale**: Matches FR-009 and the remedy table rows; no AI input is needed.

## R14 — Test strategy

- **Decision**: Unit tests for `RemedyPolicyEvaluator` (every row, limit boundaries 59/60/61%,
  attempt counts 1/2/3, missing rules), the `Repair` transition table and the remedy-rules seed
  validation. Integration tests create approved claims directly through the domain (no AI run)
  for fulfilment, concurrency, notification-uniqueness, RLS and API scenarios; one replay
  scenario reuses the S1 recordings (Aurora auto-approval) and asserts remedy Repair with a
  suggested centre. Frontend tests for the remedy picker, the repair panel and the claimant
  timeline. The evaluation runner is not affected.
- **Rationale**: Remedy logic needs no new AI recordings; reusing S1 proves the approval
  integration end to end.
