# Data Model: Policy-Driven Remedies and Repair-Centre Hand-over

**Feature**: `specs/002-remedy-repair-handover` | **Date**: 2026-10-14 | **Plan**: [plan.md](./plan.md)

Conventions are those of [001 data-model.md](../001-ai-claim-adjudication/data-model.md): UUID v7
IDs, `timestamptz` in UTC, money as `numeric(12,2)` in the tenant's currency, `tenant_id uuid not
null` on every tenant-owned table with an EF Core query filter and the RLS policy that
`001_roles_and_rls.sql` creates for every table with a `tenant_id` column, and composite foreign
keys that include `tenant_id`. New tables go into the existing `claims` and `audit` schemas, so
the existing grants, RLS loop and append-only trigger cover them without new SQL.

## Overview

```text
policy.policy_versions ── + remedy_rules (jsonb, nullable)
integration.service_centers ── + address, opening_hours, contact, is_active, priority
integration.notifications ── + dedupe_key (unique per tenant)
claims.claims ── + has_protection_plan, protection_plan_confirmed
            │
            ├─1 claims.claim_remedies 1─* claims.remedy_decisions
            │                         └─0..1 claims.remedy_completions
            └─0..1 claims.repairs 1─┬─* claims.repair_handovers   (one active)
                                    ├─* claims.repair_attempts    (one open)
                                    └─* audit.repair_events       (append-only)
```

## Changed tables

### `policy.policy_versions`

| Field | Type | Rules |
|-------|------|-------|
| remedy_rules | jsonb, nullable | `remedies` front-matter block, validated on seed against [contracts/remedy-rules.schema.json](./contracts/remedy-rules.schema.json); null = the version gives no remedy (FR-012). Immutable once seeded (FR-007). |

Domain: `PolicyVersion.RemedyRules` of type `RemedyRules?` (record in
`Warranty.Domain/Policies/RemedyRules.cs`, with `Validate()` like `CoverageTerms`).

`remedy_rules` structure (Borealis example):

```json
{
  "coveredDefect":       { "remedy": "Repair" },
  "repairNotEconomical": { "limitPercentOfClaimValue": 60, "allowed": ["Refund", "Replacement"] },
  "repairFailed":        { "afterFailedAttempts": 2, "remedy": "Refund" },
  "protectionPlan":      { "liftsAccidentalDamageExclusion": true, "remedy": "Repair" }
}
```

A row holds either `remedy` (exactly one, set automatically) or `allowed` (two or more, a
reviewer chooses). Validation: every row is optional; `remedy` and `allowed` are mutually
exclusive; `allowed` has 2–3 distinct values; `limitPercentOfClaimValue` is 1–100;
`afterFailedAttempts` is 1–5; `protectionPlan.remedy`/`allowed` is required when
`liftsAccidentalDamageExclusion` is true.

### `integration.service_centers` (repair centres)

| Field | Type | Rules |
|-------|------|-------|
| address | text | required for active centres; shown to claimants |
| opening_hours | text | required for active centres; shown to claimants, e.g. `Mon–Fri 09:00–17:00` |
| contact | text | staff-only phone/email of the centre |
| is_active | bool | default true; inactive centres are never suggested or selectable |
| priority | int | default 100; lower value = suggested first; ties broken by name |

Existing fields (`region`, `name`, `capabilities`) are unchanged. Domain: `ServiceCenter` gains the
fields; the Application port `IServiceNetwork` gains `ListEligibleAsync(region, category)`.

### `integration.notifications`

| Field | Type | Rules |
|-------|------|-------|
| dedupe_key | text, nullable | unique index `(tenant_id, dedupe_key)` where not null; `repair-ready:{attemptId}` for the ready notification (research R7) |

New template: `RepairReadyForCollection`. Existing templates are unchanged.

### `claims.claims`

| Field | Type | Rules |
|-------|------|-------|
| has_protection_plan | bool | default false; set at submission (FR-036) |
| protection_plan_confirmed | bool, nullable | set by the approving reviewer (research R9); null until reviewed |

New evidence kind on `claims.claim_evidence.kind`: `ProtectionPlanDocument` (PDF or image, same
upload sanitizing as invoices, 001 R11). New requested item code: `PROTECTION_PLAN_DOCUMENT`. New
guardrail escalation reason: `PROTECTION_PLAN_TO_CHECK`.

## New tables

### `claims.claim_remedies` (current remedy, one per approved claim)

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id, claim_id | uuid | unique `(tenant_id, claim_id)`; claim must be `Approved` |
| policy_version_id | uuid | the claim's applicable version (001 version-selection rule) |
| scenario | enum `CoveredDefect`, `ProtectionPlanDamage`, `RepairNotEconomical`, `RepairFailed` | scenario of the latest evaluation |
| state | enum `AwaitingDecision`, `InProgress`, `Completed` | see state machine below |
| remedy | enum `Repair`, `Replacement`, `Refund`, nullable | null only while `AwaitingDecision` |
| allowed_remedies | text[] | from the latest evaluation; a decision must pick one of these |
| pending_reason | text, nullable | reason code while `AwaitingDecision`: `POLICY_CHOICE`, `NO_REMEDY_RULE`, `REPAIR_FAILED_LIMIT`, `REPAIR_NOT_ECONOMICAL` |
| xmin (system column) | xid | concurrency token mapped as row version, as `claims.claims` (ClaimConfigurations); exposed as the remedy ETag |
| updated_at | timestamptz | |

### `claims.remedy_decisions` (append-only by convention; history of the remedy)

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id, claim_id | uuid | |
| seq | int | per claim, from 1 |
| kind | enum `Evaluated`, `Decided` | `Evaluated` = evaluator output; `Decided` = remedy set |
| scenario | enum (as above) | |
| policy_version_id | uuid | |
| table_row | text | `coveredDefect`, `repairNotEconomical`, `repairFailed`, `protectionPlan`, or `none` |
| allowed_remedies | text[] | |
| remedy | enum, nullable | set for `Decided` |
| decided_by | enum `Policy`, `Reviewer`, nullable | `Policy` = automatic single remedy |
| actor | text | `system` or the reviewer's `sub` |
| reason | text, nullable | required for a reviewer change after the first remedy (FR-015), 10–1000 chars |
| failed_attempts | int | count at evaluation time |
| estimate_amount | numeric(12,2), nullable | estimate used by `RepairNotEconomical` |
| created_at | timestamptz | |

Rows are only inserted. The app role keeps UPDATE on the `claims` schema, so immutability is
enforced in the repository (no update method) and checked by a unit test.

### `claims.remedy_completions`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id, claim_id | uuid | unique `(tenant_id, claim_id)` |
| remedy | enum `Replacement`, `Refund` | must equal the current remedy |
| completed_on | date | not in the future |
| reference | text, nullable | required for Refund (refund reference from the refund option, FR-035) |
| refund_request_id | uuid, nullable | ID returned by `IRefundService` |
| recorded_by | text | staff `sub` |
| recorded_at | timestamptz | |

A completed Repair has no row here; its completion is the `Collected` status.

### `claims.repairs`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id, claim_id | uuid | unique `(tenant_id, claim_id)`; created when the remedy becomes Repair |
| status | enum `AwaitingHandover`, `AtRepairCentre`, `ReadyForCollection`, `Collected`, `RepairNotPossible` | see state machine |
| not_possible_reason | enum `Failed`, `NotEconomical`, nullable | set with `RepairNotPossible` |
| active_handover_id | uuid, nullable | FK to `repair_handovers` |
| current_attempt_id | uuid, nullable | FK to `repair_attempts` |
| failed_attempts | int | default 0; never decremented except by a correction of a failure |
| xmin (system column) | xid | concurrency token mapped as row version; exposed as the repair ETag |
| created_at, updated_at | timestamptz | |

### `claims.repair_handovers`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id, repair_id | uuid | |
| service_center_id | uuid | composite FK `(tenant_id, service_center_id)`; active centre of the same tenant |
| assignment | enum `Suggested`, `ChosenByStaff`, `Transfer` | how the centre was assigned |
| handed_over_on | date, nullable | null until the hand-over is recorded |
| centre_reference | text, nullable | the centre's own job reference, ≤ 100 chars; staff-only |
| note | text, nullable | ≤ 1000 chars; staff-only |
| reason | text, nullable | required for `ChosenByStaff` and `Transfer` |
| is_active | bool | exactly one active row per repair (partial unique index) |
| recorded_by | text | staff `sub` or `system` for a suggestion |
| recorded_at | timestamptz | |

### `claims.repair_attempts`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id, repair_id | uuid | |
| seq | int | per repair, from 1 |
| service_center_id | uuid | centre of the attempt |
| result | enum `Open`, `Ready`, `Collected`, `Failed`, `NotEconomical` | `Open` until the attempt ends |
| failure_reason | text, nullable | required for `Failed` and `NotEconomical`; staff-only |
| estimate_amount | numeric(12,2), nullable | latest reported estimate |
| started_at, ended_at | timestamptz | |

Only `Failed` increments `repairs.failed_attempts` (FR-029). A transfer ends nothing: the attempt
continues at the new centre and its `service_center_id` is updated by the transfer event.

### `audit.repair_events` (append-only)

| Field | Type | Rules |
|-------|------|-------|
| id | bigserial | PK |
| tenant_id, repair_id, claim_id | uuid | |
| seq | int | per repair, gap-free |
| type | enum `CentreSuggested`, `CentreChanged`, `HandedOver`, `EstimateReported`, `MarkedReady`, `Collected`, `RepairFailed`, `RepairNotEconomical`, `NewAttemptStarted`, `Transferred`, `Correction` | |
| from_status, to_status | enum, nullable | |
| reported_at | timestamptz | when the repair centre reported it; not in the future, not before the hand-over (FR-025) |
| recorded_at | timestamptz | server time |
| source | enum `Phone`, `Email`, `InPerson`, `Other`, `System` | |
| actor | text | staff `sub` or `system` |
| note | text, nullable | staff-only |
| payload | jsonb | event details (centre IDs, estimate, reference, corrected values) |
| corrects_event_id | bigint, nullable | set for `Correction` |

The existing `audit` grants (INSERT, SELECT) and `append_only` trigger apply.

## State machines

### Remedy (`claims.claim_remedies.state`)

```text
(claim approved) ──evaluate──▶ InProgress            one remedy allowed: set by policy
                 └──────────▶ AwaitingDecision       choice, no rule, or missing information
AwaitingDecision ──reviewer decides──▶ InProgress
InProgress ──repair failed at limit / not economical──▶ AwaitingDecision
InProgress ──Collected (Repair) / completion recorded (Replacement, Refund)──▶ Completed
```

A remedy change always passes `AwaitingDecision` and a reviewer. `Completed` is final.

### Repair (`claims.repairs.status`) — FR-024

| From | Event | To | Rules |
|------|-------|----|-------|
| — | remedy becomes Repair | AwaitingHandover | suggested centre assigned (`CentreSuggested`), or none when no eligible centre |
| AwaitingHandover | `CentreChanged` | AwaitingHandover | eligible same-tenant centre, reason required |
| AwaitingHandover | `HandedOver` | AtRepairCentre | an active centre is required; opens attempt 1 when none is open |
| AtRepairCentre | `EstimateReported` | AtRepairCentre, or RepairNotPossible (`NotEconomical`) | not economical only when the version has a limit and estimate > limit × claim value |
| AtRepairCentre | `MarkedReady` | ReadyForCollection | queues the ready notification once per attempt |
| ReadyForCollection | `Collected` | Collected | attempt result `Collected`; remedy `Completed` |
| AtRepairCentre | `RepairFailed` | RepairNotPossible (`Failed`) | reason required; failed_attempts + 1; re-evaluation |
| RepairNotPossible (`Failed`, below limit) | `NewAttemptStarted` | AtRepairCentre or AwaitingHandover | product stays at the centre, or was returned to the customer |
| AwaitingHandover, AtRepairCentre | `Transferred` | AwaitingHandover | new active hand-over at another eligible centre, reason required; attempt count unchanged |
| any except Collected | `Correction` | as corrected | reason required; references the corrected event; never a second ready notification for the same attempt |

`Collected` is final. `RepairNotPossible` at the limit, or with `NotEconomical`, is final for the
repair; the remedy moves to `AwaitingDecision`. Every other combination is refused with
`409 Conflict` naming the current status and the allowed events.

## Claimant projection (no new table)

The claimant status view (`ClaimantClaimView.remedy`) is computed from `claim_remedies`,
`repairs`, the active hand-over's centre (name, address, opening hours only) and
`remedy_completions`. It never includes notes, centre references, failure reasons, staff names or
risk data (FR-041). Labels:

| State | Claimant label |
|-------|----------------|
| Remedy `AwaitingDecision` (first decision) | We are deciding how to resolve your claim |
| Repair `AwaitingHandover` | Take your product to the repair centre |
| Repair `AtRepairCentre` | With repair centre |
| Repair `ReadyForCollection` | Ready for collection |
| Repair `Collected` | Collected |
| Repair `RepairNotPossible`, remedy `AwaitingDecision` | Repair not possible. Your claim is being reviewed. |
| Replacement `InProgress` / `Completed` | Replacement on its way / Replacement handed over (*superseded by feature 003's replacement labels*) |
| Refund `InProgress` / `Completed` | Refund being processed / Refund completed |

## Seed data changes (FR-044 – FR-046)

| File | Change |
|------|--------|
| `seed/tenants/aurora/policies/AUR-WP-v1.md`, `AUR-WP-v2.md` | front-matter `remedies`: `coveredDefect: Repair`; `repairFailed: { afterFailedAttempts: 2, remedy: Replacement }`; `protectionPlan: { liftsAccidentalDamageExclusion: false }`; no `repairNotEconomical` |
| `seed/tenants/borealis/policies/BOR-WP-v1.md` | front-matter `remedies` as in the example above |
| `seed/tenants/*/service-centers.json` | add `address`, `openingHours`, `contact`, `priority`; add one more active centre per region so each region has at least two (Aurora NA and EU, Borealis EU) |
| `seed/tenants/*/remedy-scenarios.json` (new) | per tenant: approved claims in each scenario for the demo and integration tests, including one "same facts, different company" pair |

Clause text and clause keys stay unchanged (research R12).

## Domain types (summary)

| Type | Project / folder | Notes |
|------|------------------|-------|
| `RemedyType`, `RemedyScenario`, `RemedyState`, `RemedyRules` | `Warranty.Domain/Policies`, `Warranty.Domain/Fulfillment` | enums and the rules record |
| `ClaimRemedy`, `RemedyDecision`, `RemedyCompletion` | `Warranty.Domain/Fulfillment` | remedy aggregate and its history |
| `Repair`, `RepairHandover`, `RepairAttempt`, `RepairEvent`, `RepairStatus` | `Warranty.Domain/Fulfillment` | state machine (table above) |
| `RemedyPolicyEvaluator`, `RemedyEvaluation` | `Warranty.Guardrails/Remedies` | pure, deterministic (research R2) |
| `TrailStep` additions | `Warranty.Domain/Common/TrailStep.cs` | `RemedyRecommended`, `RemedyDecided`, `RepairCentreAssigned`, `RepairHandedOver`, `RepairStatusUpdated`, `RepairAttemptEnded`, `RemedyCompleted` |
| `EscalationReason.ProtectionPlanToCheck`, `EvidenceKind.ProtectionPlanDocument`, requested item `PROTECTION_PLAN_DOCUMENT` | `Warranty.Domain/Claims` | research R9 |
