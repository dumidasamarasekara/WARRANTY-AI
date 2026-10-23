# Data Model: Replacement Fulfilment and Faulty-Product Return

**Feature**: `specs/003-replacement-fulfilment` | **Date**: 2026-10-23 | **Plan**: [plan.md](./plan.md)

Conventions are those of [001 data-model.md](../001-ai-claim-adjudication/data-model.md) and
[002 data-model.md](../002-remedy-repair-handover/data-model.md): UUID v7 IDs, `timestamptz` in
UTC, `tenant_id uuid not null` on every tenant-owned table with an EF Core query filter and the
RLS policy that `001_roles_and_rls.sql` creates for every table with a `tenant_id` column, and
composite foreign keys that include `tenant_id`. New tables go into the existing `claims`,
`catalog` and `audit` schemas, so the existing grants, RLS loop and append-only trigger cover them
without new SQL.

## Overview

```text
policy.policy_versions.remedy_rules ── + replacement {…}, replacementNotAvailable   (002 column)
integration.service_centers ── + issues_replacements                                 (002 table)
integration.notifications.dedupe_key ── replacement-ready:{unitId}                   (002 column)
claims.claim_remedies ── scenario + ReplacementNotAvailable; pending_reason + REPLACEMENT_NOT_AVAILABLE
claims.claims
   │
   └─0..1 claims.replacements 1─┬─* claims.replacement_locations     (one active)
                                ├─0..1 claims.faulty_product_returns
                                ├─* claims.replacement_model_proposals (one pending)
                                ├─* claims.replacement_units           (one current)
                                └─* audit.replacement_events           (append-only)
catalog.replacement_serials ── handed-out serial ↔ original serial, claim, cover end
catalog.product_serials ── + handed-out serials not yet in the catalog
catalog.products ── + AUR-TAB11 (seed)
```

## Changed (002) structures

### `policy.policy_versions.remedy_rules` (jsonb, from 002)

New optional members, validated by
[contracts/remedy-rules.replacement.schema.json](./contracts/remedy-rules.replacement.schema.json):

```json
{
  "replacement": {
    "productRule": "SameCategoryAtLeastClaimValue",
    "returnRule": "AtCollection",
    "minimumCoverMonths": 0
  },
  "replacementNotAvailable": { "remedy": "Refund" }
}
```

| Member | Values | Rules |
|--------|--------|-------|
| `replacement.productRule` | `SameModel`, `SameCategoryAtLeastClaimValue` | default `SameModel` |
| `replacement.returnRule` | `AtCollection`, `BeforeReservation` | default `AtCollection` |
| `replacement.minimumCoverMonths` | integer 0–36 | default 0 (remainder of the original warranty only) |
| `replacementNotAvailable` | `remedy` or `allowed`, as the 002 rows | missing → `NO_REMEDY_RULE` |

Domain: `RemedyRules.Replacement` (`ReplacementRules` record with `Validate()`) and
`RemedyRules.ReplacementNotAvailable`.

### `integration.service_centers`

| Field | Type | Rules |
|-------|------|-------|
| issues_replacements | bool | default false; only these centres are replacement locations (FR-007) |

### `claims.claim_remedies` and `claims.remedy_decisions` (002)

- `scenario` gains `ReplacementNotAvailable`; `table_row` gains `replacementNotAvailable`.
- `pending_reason` gains `REPLACEMENT_NOT_AVAILABLE`.
- Remedy state machine: `InProgress ──replacement not available──▶ AwaitingDecision` and
  `InProgress ──HandedToCustomer──▶ Completed` are added.

### `claims.remedy_completions` (002)

New rows only for `Refund` (research R12). Existing `Replacement` rows are kept.

## New tables

### `claims.replacements`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id, claim_id | uuid | unique `(tenant_id, claim_id)`; created when the remedy becomes Replacement |
| policy_version_id | uuid | version whose `replacement` rules apply |
| product_rule, return_rule | enum | copied from the rules at creation (defaults when absent) |
| status | enum `AwaitingReturn`, `AwaitingUnit`, `ReadyForCollection`, `HandedToCustomer`, `NotAvailable`, `Cancelled` | see state machine |
| return_state | enum `NotYetReturned`, `Returned`, `HeldByRepairCentre`, `Mismatch`, `Accepted` | `Accepted` = mismatch accepted by a reviewer |
| active_location_id | uuid, nullable | FK to `replacement_locations` |
| current_unit_id | uuid, nullable | FK to `replacement_units` (claims schema) |
| pending_proposal_id | uuid, nullable | FK to `replacement_model_proposals` |
| approved_product_id | uuid, nullable | model approved by a reviewer; null = same model only |
| xmin (system column) | xid | concurrency token; exposed as the replacement ETag |
| created_at, updated_at | timestamptz | |

### `claims.replacement_locations`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id, replacement_id | uuid | |
| service_center_id | uuid | composite FK; active, same tenant, `issues_replacements` |
| assignment | enum `Suggested`, `ChosenByStaff`, `Moved` | |
| reason | text, nullable | required for `ChosenByStaff` and `Moved`, 10–1000 chars |
| is_active | bool | exactly one active row per replacement (partial unique index) |
| recorded_by | text | staff `sub` or `system` |
| recorded_at | timestamptz | |

### `claims.faulty_product_returns`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id, replacement_id | uuid | unique `(tenant_id, replacement_id)`; corrections go through events |
| returned_on | date | not in the future |
| service_center_id | uuid, nullable | location that received it; null when held by a repair centre |
| serial_seen | text, nullable | as reported by the location; required unless held by a repair centre |
| serial_matches | bool | `EvidenceMatchRules` comparison with the claim's serial (001 FR-016) |
| repair_handover_id | uuid, nullable | set when held by a repair centre (FR-020, research R9) |
| note | text, nullable | staff-only, ≤ 1000 |
| recorded_by, recorded_at | text, timestamptz | |

### `claims.replacement_model_proposals`

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id, replacement_id | uuid | at most one `Pending` per replacement |
| product_id | uuid | composite FK to `catalog.products`; same category, claim value ≥ original (FR-023) |
| reason | text | 10–1000 |
| state | enum `Pending`, `Approved`, `Declined` | |
| proposed_by, proposed_at | text, timestamptz | staff `sub` |
| decided_by, decided_at, decision_reason | text, timestamptz, text, nullable | reviewer; reason required for `Declined` |

### `claims.replacement_units` (units reserved for the customer)

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK; used in the notification dedupe key |
| tenant_id, replacement_id | uuid | |
| seq | int | per replacement, from 1 |
| service_center_id | uuid | location holding the unit |
| product_id | uuid | the claimed product, or `approved_product_id` |
| serial_number | text | normalised (`ProductSerial.NormalizeSerial`); ≠ the claim's serial; not in `catalog.replacement_serials` and not the serial of any claim of the tenant; if in `catalog.product_serials`, registered to `product_id` (FR-022) |
| result | enum `Reserved`, `HandedOver`, `Refused`, `Released`, `Cancelled` | `Released` = location moved |
| result_reason | text, nullable | required for `Refused`; staff-only |
| reserved_at, ended_at | timestamptz | |

### `catalog.replacement_serials` (registry for later claims)

| Field | Type | Rules |
|-------|------|-------|
| id | uuid | PK |
| tenant_id | uuid | |
| serial_number | text | unique `(tenant_id, serial_number)`; inserted into `catalog.product_serials` too when not yet there |
| product_id | uuid | product of the handed-out unit |
| original_serial | text | the claim's serial; index `(tenant_id, original_serial)` |
| claim_id | uuid | composite FK to `claims.claims` |
| original_purchase_date | date | the claim's purchase date |
| handed_over_on | date | |
| cover_end_date | date | `max(original coverage end, handed_over_on + minimumCoverMonths)` at hand-over; informational, recomputed by the guardrails (research R11) |
| created_at | timestamptz | |

A replacement of a replacement chains through `original_serial`; the original purchase date is
carried forward.

### `audit.replacement_events` (append-only)

| Field | Type | Rules |
|-------|------|-------|
| id | bigserial | PK |
| tenant_id, replacement_id, claim_id | uuid | |
| seq | int | per replacement, gap-free |
| type | enum, see state machine | |
| from_status, to_status | enum, nullable | |
| reported_at | timestamptz | not in the future, not before the remedy was set or the step it follows (FR-015) |
| recorded_at | timestamptz | server time |
| source | enum `Phone`, `Email`, `InPerson`, `Other`, `System` | |
| actor | text | staff `sub` or `system` |
| note | text, nullable | staff-only |
| payload | jsonb | location, model, serial, reason, corrected values |
| corrects_event_id | bigint, nullable | set for `Correction` |

## State machine (`claims.replacements.status`) — FR-012 to FR-014

| From | Event | Role | To | Rules |
|------|-------|------|----|-------|
| — | remedy becomes Replacement | system | `AwaitingReturn` or `AwaitingUnit` | `LocationSuggested`; `AwaitingReturn` only for `BeforeReservation` without a held product (research R9) |
| `AwaitingReturn`, `AwaitingUnit` | `LocationChanged` | agent, reviewer | unchanged | eligible location, reason |
| `AwaitingReturn` | `ReturnRecorded` | agent, reviewer | `AwaitingUnit`, or unchanged with `return_state = Mismatch` | serial compared with the claim's serial (FR-019) |
| `AwaitingUnit`, `ReadyForCollection` | `ReturnRecorded` (`AtCollection` only) | agent, reviewer | unchanged | allowed before or with the hand-over |
| any with `return_state = Mismatch` | `ReturnAccepted` | reviewer | `AwaitingUnit` from `AwaitingReturn`, otherwise unchanged | reason; not the submitter; `return_state = Accepted` |
| `AwaitingUnit` | `ModelProposed` | agent, reviewer | unchanged | `productRule = SameCategoryAtLeastClaimValue`; `ReplacementPolicy.CheckModel` passes; none pending |
| `AwaitingUnit` | `ModelApproved` / `ModelDeclined` | reviewer | unchanged | pending proposal; decline needs a reason; not the submitter |
| `AwaitingUnit` | `UnitReserved` | agent, reviewer | `ReadyForCollection` | model = claimed or approved; serial rules; no unresolved mismatch; queues the ready notification once per unit |
| `ReadyForCollection` | `UnitRefused` | agent, reviewer | `AwaitingUnit` | reason; unit `Refused` |
| `AwaitingReturn`, `AwaitingUnit`, `ReadyForCollection` | `Moved` | agent, reviewer | `AwaitingUnit` (or stays `AwaitingReturn`) | eligible location, reason; current unit `Released` |
| `ReadyForCollection` | `HandedToCustomer` | agent, reviewer | `HandedToCustomer` | `return_state` not `Mismatch`; under `AtCollection`: `returnConfirmed: true` unless held/returned; registers the serial (research R10); remedy `Completed` |
| `AwaitingUnit` | `NotAvailable` | agent, reviewer | `NotAvailable` | reason; evaluator `ReplacementNotAvailable`; remedy `AwaitingDecision` |
| any except `HandedToCustomer` | `Cancelled` | system | `Cancelled` | only from a reviewer's remedy change (research R13) |
| any except `HandedToCustomer`, `Cancelled` | `Correction` | agent, reviewer | as corrected | reason; references the corrected event; never a second notification for the same unit |

`HandedToCustomer`, `NotAvailable` and `Cancelled` are final for the replacement. Every other
combination is refused with `409 Conflict` naming the current status and the allowed events.

## Claimant projection (no new table)

`ClaimantClaimView.remedy` (002) is extended from `replacements`, the active location (name,
address, opening hours only) and the current unit's model name. It never includes notes, serial
numbers, return check results, reasons or staff names (FR-034).

| State | Claimant label |
|-------|----------------|
| `AwaitingReturn` | Please bring your product to the location below |
| `AwaitingUnit` (with or without a pending proposal or return check) | We are preparing your replacement |
| `ReadyForCollection` | Your replacement is ready for collection |
| `HandedToCustomer` | Replacement handed over |
| `NotAvailable`, remedy `AwaitingDecision` | Replacement not possible. Your claim is being reviewed. |
| `Cancelled` | (the new remedy's label, 002) |

Timeline steps: `Product returned` (only when the return rule is `BeforeReservation`, or when the
product was held by a repair centre) · `Ready for collection` · `Handed to you`.

## Seed data changes (FR-037 – FR-039)

| File | Change |
|------|--------|
| `seed/tenants/aurora/policies/AUR-WP-v1.md`, `AUR-WP-v2.md` | `remedies.replacement: { productRule: SameCategoryAtLeastClaimValue, returnRule: AtCollection, minimumCoverMonths: 0 }`; `remedies.replacementNotAvailable: { remedy: Refund }` |
| `seed/tenants/borealis/policies/BOR-WP-v1.md` | `remedies.replacement: { productRule: SameModel, returnRule: BeforeReservation, minimumCoverMonths: 6 }`; `remedies.replacementNotAvailable: { remedy: Refund }` |
| `seed/tenants/*/service-centers.json` | `issuesReplacements: true` on every active centre |
| `seed/tenants/aurora/products.json`, `serials.json` | new model `AUR-TAB11` (tablet, claim value 520); no stock serials are seeded — scenarios use new serials, and catalog serials of the right model are also accepted (FR-022) |
| `seed/tenants/*/replacement-scenarios.json` (new) | approved claims with remedy Replacement for each scenario in [quickstart.md](./quickstart.md), including one "same facts, different company" pair |

## Domain types (summary)

| Type | Project / folder | Notes |
|------|------------------|-------|
| `ReplacementRules`, `ReplacementProductRule`, `ReplacementReturnRule` | `Warranty.Domain/Policies` | part of `RemedyRules` |
| `Replacement`, `ReplacementStatus`, `ReturnState`, `ReplacementLocation`, `FaultyProductReturn`, `ReplacementModelProposal`, `ReplacementUnit`, `ReplacementEvent` | `Warranty.Domain/Fulfillment` | state machine above |
| `RegisteredReplacementSerial` | `Warranty.Domain/Catalog` | `catalog.replacement_serials` |
| `ReplacementPolicy`, `ModelCheckResult` | `Warranty.Guardrails/Remedies` | pure (research R2) |
| `ReplacementCover` | `Warranty.Guardrails/Pipeline` (`CaseFacts`) | research R11 |
| `RiskSignalCode.ProductReturnedAndReplaced` | `Warranty.Domain/Claims` | `PRODUCT_RETURNED_AND_REPLACED` |
| `TrailStep` additions | `Warranty.Domain/Common/TrailStep.cs` | research R6 |
