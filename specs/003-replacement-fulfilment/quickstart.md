# Quickstart: Replacement Fulfilment and Faulty-Product Return

**Feature**: `specs/003-replacement-fulfilment` | **Date**: 2026-10-23 | **Plan**: [plan.md](./plan.md)

Setup, run and test commands are those of
[001 quickstart.md](../001-ai-claim-adjudication/quickstart.md) §1–§3 and §5, with feature 002
implemented. Nothing new is installed. Run with `$env:AiGateway__Mode = "replay"`; no scenario
needs a live AI call. Staff users are the Keycloak realm's synthetic users (`agent.aurora`,
`reviewer.aurora`, `auditor.aurora` and the Borealis equivalents).

## 1. What to check after `aspire run`

1. The migration service applied the 003 migration and re-seeded: the policy versions view shows the
   `replacement` rules and `replacementNotAvailable` row for `AUR-WP` v1, v2 and `BOR-WP` v1.
2. `seed/tenants/*/replacement-scenarios.json` claims exist as Approved claims with remedy
   Replacement (§2).
3. `GET /api/claims/{id}/replacement-locations` lists at least one location for each region and
   product category of each tenant (FR-038); Aurora's catalog contains `AUR-TAB11`.

## 2. Validation scenarios

Claim data for X1–X12 is in `seed/tenants/*/replacement-scenarios.json` (approved claims created
by the seed, no AI run). "Today" is the day you run it.

| # | Story | Scenario | Expected result |
|---|-------|----------|-----------------|
| X1 | US1 | Aurora `AUR-TAB10` claim, remedy Replacement (seeded) | Replacement **Awaiting replacement unit** at the suggested Aurora location of the region (priority, then name); claimant page "We are preparing your replacement" with the location |
| X2 | US1 | On X1, `agent.aurora` records **Reserve unit** with a new `AUR-TAB10` serial (source Phone) | **Ready for collection**; one `integration.notifications` row, template `ReplacementReadyForCollection`, key `replacement-ready:{unitId}`; claimant page shows ready, the model name and "Please bring your faulty product with you"; no serial |
| X3 | US1 | Repeat X2 with the old ETag, then two staff at once | One status change, one notification; repeats get `412` |
| X4 | US1 | On X2, **Record hand-over to customer** without "Faulty product returned", then with it | First `400` naming `returnConfirmed`; then **Handed to customer**, remedy **Completed**, return recorded, serial in `catalog.replacement_serials` and `catalog.product_serials`; reserving the claim's own serial or an already handed-out serial on another claim → `400` |
| X5 | US1 | On X1 before reserving, call `HandedToCustomer` via the API | `409` naming status AwaitingUnit and the allowed events |
| X6 | US2 | Borealis `BOR-SLATE11` claim, remedy Replacement (seeded) | **Awaiting return**; claimant page "Please bring your product to …"; `UnitReserved` → `409` |
| X7 | US2 | On X6, record the return with the claim's serial, then reserve and hand over | **Awaiting replacement unit** → **Ready for collection** → **Handed to customer**; no return confirmation asked at hand-over |
| X8 | US2 | On a second Borealis claim, record the return with another serial | Return stored with mismatch, status stays **Awaiting return**; claim in the review queue as **Replacement · Returned product does not match**; `reviewer.borealis` records **Accept return** with a reason → **Awaiting replacement unit** |
| X9 | US2 | Aurora claim at its second failed repair (002 R8), product at the repair centre; reviewer confirms Replacement | Replacement starts with return **Held by repair centre** (source System, referring to the repair hand-over); hand-over to customer needs no return confirmation |
| X10 | US3 | On an Aurora `AUR-TAB10` claim: propose `AUR-TAB11` (claim value 520) with a reason; reserve before approval; `reviewer.aurora` approves; reserve an `AUR-TAB11` unit | Proposal pending, claim in queue as **Model approval**; early reserve `400`; after approval **Ready for collection** with `AUR-TAB11`; claimant sees "Aurora Tab 11" |
| X11 | US3 | Propose `AUR-PHN6` (other category) on an Aurora tablet claim; propose any model on a Borealis claim | Both `400` naming the policy rule; `GET /replacement-models` is empty for Borealis |
| X12 | US3 | Record **Not available** on an Aurora and a Borealis claim | **Replacement not available**; remedy **Awaiting remedy decision**, pending `REPLACEMENT_NOT_AVAILABLE`, recommending Refund; reviewer confirms with a reason → Refund requested (002 R10), replacement history kept |
| X13 | US4 | Integration test through the deterministic intake and guardrail steps (no AI run, research R15): a new claim for the serial handed out in X4, purchase date and invoice of the original | No `PRODUCT_NOT_IN_CATALOG`; invoice serial check passes with the original serial; coverage end = original window end |
| X14 | US4 | As X13 for the X7 Borealis unit, with a claim date after the original 24 months but within 6 months of the hand-over (test clock) | Covered: coverage end = hand-over date + 6 months |
| X15 | US4 | As X13 for the faulty serial returned in X4 | Risk signal `PRODUCT_RETURNED_AND_REPLACED`; never approved automatically; goes to human review |
| X16 | US5 | On a **Ready for collection** claim: **Move** to another Aurora location; reserve a new unit there; record **Customer refused unit**; reserve again | One active location; old location and its unit `Released`; refused unit kept; a notification per reserved unit (3 rows, distinct keys); remedy unchanged |
| X17 | US5 | Correct a mistaken unit serial with a reason | Corrected serial applies; original entry struck through in the events timeline |
| X18 | FR-011 | As `agent.aurora`, `GET /replacement-locations` and `POST /replacement/events` (`Moved`) with a Borealis location ID on an Aurora claim; `POST` on a Borealis claim | All `404`; `ACCESS_DENIED` security events; no Borealis data returned |
| X19 | FR-024 | `reviewer.aurora` approves a model on a claim they submitted | `403`, `SELF_REVIEW_REFUSED` |
| X20 | SC-011 | Run the full 001 and 002 suites | All pass; 002's Replacement completion test now uses `HandedToCustomer` (research R12) |

SC-001 (staff record a step in under one minute) and SC-007 (auditor explains the replacement from
the trail in under 3 minutes) are checked by a timed walkthrough in the final validation task.

Headless example for X5 (token for `agent.aurora`, ETag from `GET /api/claims/{id}`
`replacement.etag`):

```powershell
curl -k -X POST -H "Authorization: Bearer $token" -H "If-Match: $etag" -H "Content-Type: application/json" `
  -d '{"type":"HandedToCustomer","reportedAt":"2026-10-23T09:10:00Z","source":"InPerson","handedOverOn":"2026-10-23","returnConfirmed":true}' `
  https://localhost:7443/api/claims/<claim-id>/replacement/events
# → 409 application/problem+json naming status AwaitingUnit
```

## 3. Automated tests

| Project | Tests |
|---------|-------|
| `tests/Warranty.UnitTests` | `ReplacementPolicy` (same model; claim value lower, equal, higher; other category; missing claim value; SameModel rule); evaluator row `ReplacementNotAvailable` and missing row; `Replacement` transition table (every allowed and refused pair, role rules); `CoverageWindowCalculator` with a minimum cover end; replacement rules validation; claimant projection never exposes staff-only fields |
| `tests/Warranty.IntegrationTests` | X1–X19 via the API (trait `Feature=003`); notification uniqueness under concurrency; RLS probes on every new table; append-only `audit.replacement_events`; seed checks for replacement rules, locations and `AUR-TAB11` |
| `src/web` (Vitest) | Replacement buttons follow `allowedEvents`; propose dialog lists only `replacement-models`; hand-over dialog requires the return checkbox under `AtCollection`; claimant labels and absence of serials |

```powershell
dotnet test --project tests/Warranty.UnitTests -- --filter-class "*ReplacementPolicy*"
dotnet test --project tests/Warranty.IntegrationTests -- --filter-trait "Feature=003"
```
