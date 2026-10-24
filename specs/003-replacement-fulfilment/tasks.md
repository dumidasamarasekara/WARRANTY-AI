---

description: "Task list for replacement fulfilment and faulty-product return"
---

# Tasks: Replacement Fulfilment and Faulty-Product Return

**Input**: Design documents from `/specs/003-replacement-fulfilment/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Feature prerequisite**: every task of `specs/002-remedy-repair-handover/tasks.md` (T201–T265) is
merged to `master`. This feature extends 002's remedy table, `RemedyPolicyEvaluator`,
`ActionExecutor` remedy paths, service centres, notification dedupe key, `Fulfillment` folders and
Remedy tab; none of its tasks can start before that.

**Tests**: Included. The plan's testing strategy requires unit, integration and frontend tests, and
SC-002 – SC-005 and SC-008 – SC-011 need automated verification. Within each story, write the
tests first; following CLAUDE.md, a test task lands its cases with `Skip = "Pending T3xx"` for every
implementing task not yet merged, and the implementing task removes those skips.

**Organization**: Tasks are grouped by user story (US1–US5 of spec.md). Scenario IDs X1–X20 refer
to [quickstart.md §2](./quickstart.md). Research decisions R1–R15 are written "research R*n*".

**Task IDs start at T301** so that branch names (`task/T3xx-…`) and GitHub issue titles
(`T3xx: …`) never collide with feature 001's T001–T128 or feature 002's T201–T265.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on an incomplete task)
- **[Story]**: Which user story the task belongs to (US1–US5)
- Paths follow plan.md: .NET projects in `src/Warranty.*`, SPA in `src/web`, tests in `tests/`,
  seed data in `seed/`
- New integration test classes carry `[Trait("Feature", "003")]`

## Fixed identifiers used across tasks

| Item | Value |
|------|-------|
| Aurora tenant | id `11111111-1111-7111-8111-111111111111`, policy `AUR-WP` v1 and v2 |
| Borealis tenant | id `22222222-2222-7222-8222-222222222222`, policy `BOR-WP` v1 |
| Replacement rules | Aurora: `replacement: {productRule: SameCategoryAtLeastClaimValue, returnRule: AtCollection, minimumCoverMonths: 0}`, `replacementNotAvailable: {remedy: Refund}`. Borealis: `replacement: {productRule: SameModel, returnRule: BeforeReservation, minimumCoverMonths: 6}`, `replacementNotAvailable: {remedy: Refund}` |
| Equivalent model (seed) | Aurora `AUR-TAB11`, category `tablet`, claim value 520 (equivalent of `AUR-TAB10`, 450) |
| Ready notification | template `ReplacementReadyForCollection`, dedupe key `replacement-ready:{unitId}` |
| New risk signal | `PRODUCT_RETURNED_AND_REPLACED` (severity Medium) |
| Review queue item | kind `ReplacementCheck`, reasons `MODEL_APPROVAL`, `RETURN_MISMATCH`; remedy pending reason `REPLACEMENT_NOT_AVAILABLE` |
| Canonical REST contract | `specs/001-ai-claim-adjudication/contracts/rest-api.openapi.yaml` (002 research R11) |

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Contract and schema in place before code depends on them

- [X] T301 Merge [contracts/rest-api.delta.openapi.yaml](./contracts/rest-api.delta.openapi.yaml) into the canonical `specs/001-ai-claim-adjudication/contracts/rest-api.openapi.yaml`: add the three new paths under the `Fulfillment` tag, add the new enum values to `RemedyScenario`, the remedy pending reasons and table rows, add `ReplacementCheck` to `ReviewQueueItem.kind`, restrict `RemedyCompletionRequest.remedy` to `Refund`, fold the `*Additions` schemas into `ClaimDetail`, `ReviewQueueItem` and `ClaimantRemedyView`, add the new schemas and `TrailStep` values; regenerate `src/web/src/shared/api/schema.d.ts` with `npm --prefix src/web run gen:api` and make `npm --prefix src/web run build` pass
- [X] T302 [P] Merge the `replacement` and `replacementNotAvailable` properties of [contracts/remedy-rules.replacement.schema.json](./contracts/remedy-rules.replacement.schema.json) into `src/Warranty.MigrationService/Seeding/Schemas/remedy-rules.schema.json` (the embedded copy from 002 T202) and into `specs/002-remedy-repair-handover/contracts/remedy-rules.schema.json` (research R1)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Domain types, persistence (one migration), ports, simulated adapters and seed data used by every story

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Domain and ports

- [X] T303 [P] Add `ReplacementRules` (`ProductRule`, `ReturnRule`, `MinimumCoverMonths` 0–36, defaults `SameModel`/`AtCollection`/0) and the row `ReplacementNotAvailable` (one `Remedy` or an `Allowed` list, as the 002 rows) to `src/Warranty.Domain/Policies/RemedyRules.cs` with `Validate()` rules from data-model.md
- [X] T304 [P] Add the enums `ReplacementStatus`, `ReturnState`, `ReplacementEventType`, `ReplacementProductRule`, `ReplacementReturnRule`, `LocationAssignment`, `ReplacementUnitResult`, `ModelProposalState`, the value `RemedyScenario.ReplacementNotAvailable` and the pending reason `REPLACEMENT_NOT_AVAILABLE` in `src/Warranty.Domain/Fulfillment/FulfillmentEnums.cs`
- [X] T305 [P] Create the shapes of `Replacement` (status, rules copied at creation, return state, active location, current unit, pending proposal, approved product; `Create` in `AwaitingReturn` or `AwaitingUnit` with an optional suggested location), `ReplacementLocation`, `FaultyProductReturn`, `ReplacementModelProposal`, `ReplacementUnit` and `ReplacementEvent` per data-model.md in `src/Warranty.Domain/Fulfillment/Replacement.cs`, `ReplacementLocation.cs`, `FaultyProductReturn.cs`, `ReplacementModelProposal.cs`, `ReplacementUnit.cs`, `ReplacementEvent.cs` (transition methods come in T325, T336, T343 and T355)
- [X] T306 [P] Add `ServiceCenter.IssuesReplacements` in `src/Warranty.Domain/Integration/IntegrationRecords.cs`; create `RegisteredReplacementSerial` (serial, product, original serial, claim, original purchase date, hand-over date, cover end) in `src/Warranty.Domain/Catalog/RegisteredReplacementSerial.cs`; add `RiskSignalCode.ProductReturnedAndReplaced` (`PRODUCT_RETURNED_AND_REPLACED`) in `src/Warranty.Domain/Claims/RiskSignalCode.cs`; add the `TrailStep` values `ReplacementLocationAssigned`, `FaultyProductReturned`, `ReplacementModelProposed`, `ReplacementModelDecided`, `ReplacementStatusUpdated`, `ReplacementUnitRegistered` in `src/Warranty.Domain/Common/TrailStep.cs`
- [X] T307 Add a `purpose` argument (`Repair` | `Replacement`) to `IServiceNetwork.ListEligibleAsync` in `src/Warranty.Application/Abstractions/Integrations/IntegrationPorts.cs` (002 callers pass `Repair`); add `IReplacementRepository` (get by claim with locations, return, proposals and units; add; add-only `ReplacementEvent`) and `IReplacementSerialRegistry` (find by serial, find by original serial, register, serial-in-use check) in `src/Warranty.Application/Abstractions/Persistence/Repositories.cs`

### Persistence, adapters and seed

- [X] T308 Add EF Core configurations for `replacements`, `replacement_locations`, `faulty_product_returns`, `replacement_model_proposals`, `replacement_units` (schema `claims`), `replacement_serials` (schema `catalog`) and `replacement_events` (schema `audit`) with composite tenant FKs, the `xmin` row version on `replacements`, the partial unique indexes "one active location" and "one pending proposal" per replacement, and unique `(tenant_id, serial_number)` / index `(tenant_id, original_serial)` on `catalog.replacement_serials`, in `src/Warranty.Infrastructure/Persistence/Configurations/ReplacementConfigurations.cs`; add `service_centers.issues_replacements` to the existing configuration and the `DbSet`s in `src/Warranty.Infrastructure/Persistence/WarrantyDbContext.cs`
- [X] T309 Generate the migration `AddReplacements` in `src/Warranty.Infrastructure/Persistence/Migrations/` (after 002's migration; one migration for the whole feature, regenerated on a snapshot conflict as CLAUDE.md says) and confirm the existing `001_roles_and_rls.sql` loop gives every new table RLS and `audit.replacement_events` the append-only trigger without SQL changes
- [X] T310 Implement `IReplacementRepository` and `IReplacementSerialRegistry` in `src/Warranty.Infrastructure/Persistence/Repositories/ReplacementRepositories.cs` (no update or delete for `ReplacementEvent` and registered serials) and register them in the Infrastructure DI
- [X] T311 [P] Make `src/Warranty.Integrations.Simulated/SimulatedServiceNetwork.cs` honour `purpose` (`Replacement` requires `IssuesReplacements`) and add the template `ReplacementReadyForCollection` to `src/Warranty.Integrations.Simulated/SimulatedNotificationService.cs`
- [ ] T312 Read and validate the `replacement` members (schema from T302, then `RemedyRules.Validate`), `issuesReplacements` on service centres and new products in `src/Warranty.MigrationService/Seeding/SeedFiles.cs` and `src/Warranty.MigrationService/Seeding/TenantSeeder.cs`
- [X] T313 [P] Add the replacement rules from "Fixed identifiers" to the `remedies` blocks of `seed/tenants/aurora/policies/AUR-WP-v1.md`, `AUR-WP-v2.md` and `seed/tenants/borealis/policies/BOR-WP-v1.md` without changing clause text or keys (002 research R12); set `issuesReplacements: true` on every active centre in `seed/tenants/aurora/service-centers.json` and `seed/tenants/borealis/service-centers.json`; add `AUR-TAB11` to `seed/tenants/aurora/products.json` (research R14)

### Foundational tests

- [X] T314 [P] Extend `tests/Warranty.UnitTests/Domain/RemedyRulesTests.cs` (valid schema examples; defaults when `replacement` is absent; `minimumCoverMonths` −1 or 37, unknown rule values, `remedy` and `allowed` together in `replacementNotAvailable` all throw) and `tests/Warranty.UnitTests/Seed/SeedFilesTests.cs` / `SeedDataTests.cs` for the replacement rules, `issuesReplacements` and `AUR-TAB11`
- [X] T315 [P] Extend `tests/Warranty.IntegrationTests/Isolation/RowLevelSecurityTests.cs` with raw-SQL probes as the other tenant on every new table, add an append-only check for `audit.replacement_events`, and extend `tests/Warranty.IntegrationTests/Migration/MigrationServiceTests.cs` to assert seeded replacement rules per version and at least one replacement location per region and product category (FR-038)
- [X] T316 [P] Add architecture rules that only `ActionExecutor` creates or cancels a `Replacement` and that `Warranty.Guardrails/Remedies` references no AI project in `tests/Warranty.UnitTests/Architecture/DependencyRulesTests.cs`

**Checkpoint**: migration applies, seed loads replacement rules, locations and `AUR-TAB11`, RLS covers the new tables

---

## Phase 3: User Story 1 - Replacement collection and customer notification (Priority: P1) 🎯 MVP

**Goal**: A Replacement remedy gets a suggested location; staff record the reserved unit, the customer is notified once, and the hand-over completes the remedy and registers the unit

**Independent Test**: quickstart X1–X5 and X18: an Aurora same-model replacement from suggestion to hand-over, one notification under repeats and concurrent staff, refused out-of-order steps, no cross-tenant locations

### Tests for User Story 1 ⚠️ write first, ensure they fail

- [X] T317 [P] [US1] Write `tests/Warranty.UnitTests/Guardrails/Remedies/ReplacementPolicyTests.cs`: `InitialStatus` for both return rules with and without a held product; `CheckModel` cases land skipped as `Pending T342`
- [ ] T318 [P] [US1] Write `tests/Warranty.UnitTests/Fulfillment/ReplacementStateMachineTests.cs`: every status × event pair of the data-model.md table is allowed or refused as listed, including role rules (US2/US3/US5 events skipped as `Pending T336`/`Pending T343`/`Pending T355`); `AllowedEvents` per status and role; `HandedToCustomer` is final
- [ ] T319 [P] [US1] Write `tests/Warranty.UnitTests/Application/RecordReplacementEventTests.cs`: `reportedAt` in the future or before the step it follows → 400; serial equal to the claim's, already handed out, or a catalog serial of another model → 400 (FR-022); missing `returnConfirmed` under `AtCollection` → 400; other tenant's location → not found + `ACCESS_DENIED`; stale `If-Match` → 412; refused transition → 409 naming status and allowed events; `UnitReserved` queues one notification with key `replacement-ready:{unitId}`; `HandedToCustomer` completes the remedy and registers the serial
- [ ] T320 [P] [US1] Write `tests/Warranty.UnitTests/Fulfillment/ClaimantReplacementProjectionTests.cs`: labels and timeline steps of data-model.md "Claimant projection"; the projection never contains serial numbers, notes, return check results, reasons, contact details or staff names (FR-034)
- [ ] T321 [P] [US1] Create `seed/tenants/aurora/replacement-scenarios.json` and `seed/tenants/borealis/replacement-scenarios.json` with the approved claims whose remedy is Replacement for X1 (and placeholders for X6–X17 completed in later stories), loaded by a new `src/Warranty.MigrationService/Seeding/ReplacementScenarioSeeder.cs` through the same domain methods as the executor (seeded history like `remedy-scenarios.json`)
- [ ] T322 [P] [US1] Write `tests/Warranty.IntegrationTests/Fulfillment/US1_ReplacementCollectionTests.cs` for X1–X5 (parallel requests prove one status change and one notification row) and X18

### Implementation for User Story 1

- [ ] T323 [US1] Implement `ReplacementPolicy.InitialStatus` in `src/Warranty.Guardrails/Remedies/ReplacementPolicy.cs` and register it in `src/Warranty.Guardrails/DependencyInjection.cs`
- [ ] T324 [US1] In `src/Warranty.Application/Actions/ActionExecutor.cs`, when the remedy becomes Replacement (approval and `ExecuteRemedyDecisionAsync`), create the `Replacement` in the same transaction with the rules of the claim's policy version, the first `ListEligibleAsync(…, Replacement)` location (`LocationSuggested` event, trail `ReplacementLocationAssigned`) and the initial status (research R3)
- [ ] T325 [US1] Implement `ChangeLocation`, `ReserveUnit`, `RecordReturn` (under `AtCollection`), `HandToCustomer` and `AllowedEvents(role)` on `Replacement` and `ReplacementUnit` (emitting `ReplacementEvent`s) in `src/Warranty.Domain/Fulfillment/Replacement.cs` and `ReplacementUnit.cs`
- [ ] T326 [US1] Implement `src/Warranty.Application/Fulfillment/RecordReplacementEvent.cs` for `LocationChanged`, `UnitReserved`, `ReturnRecorded` and `HandedToCustomer`: roles claims-agent and claims-reviewer, `If-Match` on the replacement, location eligibility through `IServiceNetwork`, serial rules through `IReplacementSerialRegistry` and the catalog, one transaction writing the replacement, `audit.replacement_events` and a trail entry, the ready notification with its dedupe key, and on hand-over the remedy `Completed` plus `catalog.replacement_serials` (and `catalog.product_serials` when absent) with the cover end (research R10)
- [ ] T327 [US1] Restrict `src/Warranty.Application/Fulfillment/RecordRemedyCompletion.cs` to Refund (research R12) and move the 002 Replacement completion tests (`tests/Warranty.UnitTests/Application/RecordRemedyCompletionTests.cs`, `tests/Warranty.IntegrationTests/Fulfillment/US4_RemedyCompletionTests.cs`) to `HandedToCustomer`; change the Replacement case of `src/web/src/features/remedy/CompletionCard.tsx` to defer to the Replacement card
- [ ] T328 [US1] Create `ReplacementView` (with `allowedEvents` for the caller's role and events with staff display names) and the eligible-location query in `src/Warranty.Application/Fulfillment/ReplacementQueries.cs`; add `replacement` to the claim detail in `src/Warranty.Application/Claims/ClaimQueries.cs`
- [ ] T329 [US1] Add `GET /api/claims/{claimId}/replacement-locations` (any staff role) and `POST /api/claims/{claimId}/replacement/events` (claims-agent, claims-reviewer) to `src/Warranty.Api/Endpoints/Fulfillment/FulfillmentEndpoints.cs` with the error mapping of plan.md
- [ ] T330 [US1] Extend `src/Warranty.Application/Fulfillment/ClaimantRemedyProjection.cs` with the replacement labels, `location`, `replacementModelName` and `replacementSteps`
- [ ] T331 [P] [US1] Add the replacement labels and tones of [contracts/ui-screens.md §1](./contracts/ui-screens.md) in `src/web/src/shared/presentation/replacement.ts` and labels for the new trail steps in the trace presentation used by `src/web/src/features/trace/decisionTrace.ts`, with tests next to them
- [ ] T332 [US1] Build the Replacement card, the dialogs for Change location, Reserve unit and Record hand-over to customer (required "Faulty product returned" checkbox under `AtCollection`), and the replacement events timeline in `src/web/src/features/remedy/ReplacementCard.tsx`, `ReplacementEventDialog.tsx`, `ReplacementEventsTimeline.tsx`; buttons only from `allowedEvents`; 409/412/403 messages; auditors read-only; Vitest tests
- [ ] T333 [US1] Add the replacement section (remedy line, timeline, location card, ready callout with model name and "Please bring your faulty product with you") to `src/web/src/features/claimant/ClaimStatusPage.tsx` and `claimProgress.ts`, replacing 002's "Replacement on its way"; Vitest tests including the absence of serials and staff-only fields

**Checkpoint**: US1 gives a same-model replacement from suggestion to hand-over; validate X1–X5, X18

---

## Phase 4: User Story 2 - Return of the faulty product as the policy requires (Priority: P2)

**Goal**: Return-first companies collect the faulty product before reserving a unit; mismatches go to a reviewer; a product already at a repair centre is not asked for again

**Independent Test**: quickstart X6–X9

### Tests for User Story 2 ⚠️ write first, ensure they fail

- [ ] T334 [P] [US2] Write `tests/Warranty.UnitTests/Fulfillment/ReplacementReturnTests.cs` (return before reservation; serial match with 001 FR-016 normalisation; mismatch blocks reservation and hand-over; `ReturnAccepted` reviewer-only and not by the submitter; held-by-repair-centre detection from a 002 `Repair` per research R9) and unskip the US2 cases of `ReplacementStateMachineTests.cs`; add the X6–X9 claims to `seed/tenants/*/replacement-scenarios.json`
- [ ] T335 [P] [US2] Write `tests/Warranty.IntegrationTests/Fulfillment/US2_FaultyProductReturnTests.cs` for X6–X9

### Implementation for User Story 2

- [ ] T336 [US2] Implement `RecordReturn` for `BeforeReservation` (with the serial comparison from `src/Warranty.Guardrails/Rules/EvidenceMatchRules.cs`), the `Mismatch` state and `AcceptReturn` on `Replacement` and `FaultyProductReturn` in `src/Warranty.Domain/Fulfillment/`
- [ ] T337 [US2] Extend `src/Warranty.Application/Fulfillment/RecordReplacementEvent.cs` with `ReturnRecorded` (both rules) and `ReturnAccepted` (claims-reviewer only, self-review refused with `SELF_REVIEW_REFUSED`, as 002 research R4); in `src/Warranty.Application/Actions/ActionExecutor.cs` record the `System` return referring to the repair hand-over when the product is held by a repair centre (research R9)
- [ ] T338 [US2] Add `ReplacementCheck` items with `RETURN_MISMATCH` to `src/Warranty.Application/Review/ReviewQueueQuery.cs`
- [ ] T339 [US2] Add the Record return and Accept return dialogs and the return line to `src/web/src/features/remedy/ReplacementCard.tsx` and `ReplacementEventDialog.tsx`, `ReplacementCheck` items to `src/web/src/features/review/ReviewQueuePage.tsx`, and the "Please bring your product to …" card to `src/web/src/features/claimant/ClaimStatusPage.tsx`; Vitest tests

**Checkpoint**: both return rules demonstrated; validate X6–X9

---

## Phase 5: User Story 3 - Equivalent product and replacement not available (Priority: P2)

**Goal**: Reviewers approve equivalent models where the policy allows them; "not available" leads to a reviewer-confirmed remedy change

**Independent Test**: quickstart X10–X12 and X19

### Tests for User Story 3 ⚠️ write first, ensure they fail

- [ ] T340 [P] [US3] Unskip the `CheckModel` cases of `tests/Warranty.UnitTests/Guardrails/Remedies/ReplacementPolicyTests.cs` (same model; claim value lower, equal, higher; other category; missing claim value; `SameModel` rule) and extend `tests/Warranty.UnitTests/Guardrails/Remedies/RemedyPolicyEvaluatorTests.cs` with the `ReplacementNotAvailable` row and a missing row; unskip the US3 cases of `ReplacementStateMachineTests.cs`; add the X10–X12 claims to `seed/tenants/*/replacement-scenarios.json`
- [ ] T341 [P] [US3] Write `tests/Warranty.IntegrationTests/Fulfillment/US3_EquivalentAndNotAvailableTests.cs` for X10–X12 and X19 (claim stays `Approved`; history keeps the original Replacement remedy)

### Implementation for User Story 3

- [ ] T342 [US3] Implement `ReplacementPolicy.CheckModel` in `src/Warranty.Guardrails/Remedies/ReplacementPolicy.cs` and the `ReplacementNotAvailable` scenario with pending reason `REPLACEMENT_NOT_AVAILABLE` (a single remedy still needs a reviewer) in `src/Warranty.Guardrails/Remedies/RemedyPolicyEvaluator.cs`
- [ ] T343 [US3] Implement `ProposeModel`, `ApproveModel`, `DeclineModel` and `MarkNotAvailable` on `Replacement` and `ReplacementModelProposal` in `src/Warranty.Domain/Fulfillment/`
- [ ] T344 [US3] Extend `src/Warranty.Application/Fulfillment/RecordReplacementEvent.cs` with `ModelProposed`, `ModelApproved`/`ModelDeclined` (claims-reviewer only, self-review refused) and `NotAvailable` (run `RemedyPolicyEvaluator`, move `ClaimRemedy` to `AwaitingDecision`, write an `Evaluated` remedy decision); add `ReplacementModelQuery` and `GET /api/claims/{claimId}/replacement-models` in `src/Warranty.Application/Fulfillment/ReplacementQueries.cs` and `src/Warranty.Api/Endpoints/Fulfillment/FulfillmentEndpoints.cs`; add `MODEL_APPROVAL` items and the `REPLACEMENT_NOT_AVAILABLE` label to `src/Warranty.Application/Review/ReviewQueueQuery.cs`
- [ ] T345 [US3] In `ExecuteRemedyDecisionAsync` (`src/Warranty.Application/Actions/ActionExecutor.cs`), allow a change away from Replacement until `HandedToCustomer` and end the replacement as `Cancelled` with a `System` event in the same transaction (research R13)
- [ ] T346 [US3] Add the Propose other model (lists only `/replacement-models`), Approve/Decline model and Not available dialogs and the model line to `src/web/src/features/remedy/ReplacementCard.tsx` and `ReplacementEventDialog.tsx`, and the "Replacement not possible. Your claim is being reviewed." banner to `src/web/src/features/claimant/ClaimStatusPage.tsx`; Vitest tests

**Checkpoint**: company-specific model rules and the not-available path demonstrated; validate X10–X12, X19

---

## Phase 6: User Story 4 - Warranty of the replacement unit (Priority: P3)

**Goal**: Later claims recognise replacement units and flag returned faulty units

**Independent Test**: quickstart X13–X15

### Tests for User Story 4 ⚠️ write first, ensure they fail

- [ ] T347 [P] [US4] Extend `tests/Warranty.UnitTests/Guardrails/CoverageWindowTests.cs` (minimum cover end before, on and after the original end), `tests/Warranty.UnitTests/Harness/RiskAssessorTests.cs` (`PRODUCT_RETURNED_AND_REPLACED` from intake signals, no `PRODUCT_NOT_IN_CATALOG` for a registered replacement), `tests/Warranty.UnitTests/Harness/Tools/InvoiceValidationToolTests.cs` (original serial accepted for a replacement unit only) and `tests/Warranty.UnitTests/Application/CaseKnowledgeProviderTests.cs` (replacement cover in `CaseFacts`; accidental incidents counted across the chain)
- [ ] T348 [P] [US4] Write `tests/Warranty.IntegrationTests/Fulfillment/US4_ReplacementWarrantyTests.cs` for X13–X15 through the deterministic intake and guardrail steps without an AI run (research R15), including that a claim with the new signal is never approved automatically

### Implementation for User Story 4

- [ ] T349 [US4] Look up the claimed serial in `IReplacementSerialRegistry` in `src/Warranty.Application/Adjudication/CaseKnowledgeProvider.cs`: add `ReplacementCover` to `CaseFacts` in `src/Warranty.Guardrails/Pipeline/GuardrailInput.cs` (original purchase date and cover end), the returned-and-replaced flag to the claim history counts, and count approved accidental-damage claims of the original serial in `PriorApprovedAccidental`
- [ ] T350 [US4] Add the optional `minimumCoverEnd` to `CoverageWindowCalculator.Calculate` in `src/Warranty.Guardrails/Rules/CoverageWindowCalculator.cs` (coverage end = later of the window from the original purchase date and the minimum) and pass the replacement cover where the guardrail pipeline computes the window
- [ ] T351 [US4] Accept the original serial as an alternative for a replacement unit in `src/Warranty.AI.Harness/Tools/Implementations/InvoiceValidationTool.cs` (`ClaimedPurchase`) using `EvidenceMatchRules`; check the Evidence agent prompt for serial comparisons (research R11 open item) and, if it compares serials, pass the alternative as data only — no prompt wording, schema or recording change
- [ ] T352 [US4] Add the `PRODUCT_RETURNED_AND_REPLACED` signal (Medium) to `RiskAssessor.IntakeSignals` in `src/Warranty.AI.Harness/Agents/Risk/RiskAssessor.cs` and its label to the SPA's risk-signal presentation in `src/web/src/shared/presentation/`

**Checkpoint**: later claims on replacement units handled; validate X13–X15

---

## Phase 7: User Story 5 - Location change, refused unit and corrections (Priority: P3)

**Goal**: Moves, refusals and corrections are recorded without overwriting history

**Independent Test**: quickstart X16 and X17

### Tests for User Story 5 ⚠️ write first, ensure they fail

- [ ] T353 [P] [US5] Write `tests/Warranty.UnitTests/Fulfillment/ReplacementMoveCorrectionTests.cs` (one active location after a move, current unit `Released`, refused unit kept and remedy unchanged, a notification per reserved unit, correction keeps the original event and never queues a second notification for the same unit) and unskip the US5 cases of `ReplacementStateMachineTests.cs`
- [ ] T354 [P] [US5] Write `tests/Warranty.IntegrationTests/Fulfillment/US5_MoveRefusalCorrectionTests.cs` for X16, X17 and a move to a Borealis location from an Aurora claim (404 + `ACCESS_DENIED`)

### Implementation for User Story 5

- [ ] T355 [US5] Implement `Move`, `RefuseUnit` and `Correct` on `Replacement` in `src/Warranty.Domain/Fulfillment/Replacement.cs` and `ReplacementUnit.cs`
- [ ] T356 [US5] Extend `src/Warranty.Application/Fulfillment/RecordReplacementEvent.cs` with `Moved`, `UnitRefused` and `Correction` (reason required; correction references `correctsSeq`; corrected values applied; original event untouched)
- [ ] T357 [US5] Add the Move, Customer refused unit and Correct an entry dialogs and the struck-through corrected entries in `src/web/src/features/remedy/ReplacementEventDialog.tsx` and `ReplacementEventsTimeline.tsx`; the claimant page shows the new location; Vitest tests

**Checkpoint**: validate X16, X17

---

## Phase 8: Polish & Cross-Cutting Concerns

- [ ] T358 [P] Update documentation: add feature 003 to `CLAUDE.md` (what it adds, the `Feature=003` trait, the T3xx numbering); note in `specs/002-remedy-repair-handover/spec.md` (FR-034, User Story 4 scenario 1) and `contracts/ui-screens.md` (claimant label "Replacement on its way") that feature 003 supersedes them
- [ ] T359 Run the full validation: `dotnet build Warranty.slnx`, `dotnet test --solution Warranty.slnx -- --filter-not-trait "Category=Smoke"`, the frontend lint/test/build, quickstart X1–X20 by hand, and the timed walkthroughs for SC-001 (a replacement step under one minute) and SC-007 (auditor explains the replacement from the trail in under 3 minutes); record the results in the PR description

---

## Dependencies & Execution Order

### Phase Dependencies

- **Feature 002**: T201–T265 merged before any task here
- **Setup (T301–T302)**: T301 first so the SPA types exist for every web task
- **Foundational (T303–T316)**: after Setup; blocks every story. T303–T306 in parallel; T307 after them; T308 → T309 → T310; T311–T313 after T307; tests T314–T316 alongside
- **US1 (T317–T333)**: after Foundational
- **US2 (T334–T339)**: after US1
- **US3 (T340–T346)**: after US1
- **US4 (T347–T352)**: after US1 (needs registered serials from T326); independent of US2/US3/US5
- **US5 (T353–T357)**: after US1
- **Polish (T358–T359)**: after every story in scope

### User Story Dependencies (completion order)

```text
002 merged → Setup → Foundational → US1 ─┬─► US2
                                         ├─► US3
                                         ├─► US5
                                         └─► US4
```

### Within Each User Story

- Tests and seed fixtures first, landed with `Skip = "Pending T3xx"`; the implementing task unskips them
- Domain transitions before application commands; commands before endpoints; endpoints before the UI that calls them
- `Replacement.cs`, `RecordReplacementEvent.cs`, `ReplacementCard.tsx` and `ReplacementEventDialog.tsx` are touched by US1, US2, US3 and US5 in turn: never run those tasks in parallel, even across stories

### Parallel Opportunities

- Setup: T302 alongside T301
- Foundational: T303–T306 together; T311, T312, T313 together after T307; T314–T316 alongside T308–T313
- US1: T317–T322 together; T331 alongside T323–T330; T332 and T333 after T328–T330
- After US1: US4 (guardrails, harness, adjudication files) can go to a second developer while US2, US3 and US5 run in sequence on the shared replacement files

---

## Parallel Example: User Story 1

```text
# Tests and fixtures together:
Task: "T317 ReplacementPolicy tests in tests/Warranty.UnitTests/Guardrails/Remedies/ReplacementPolicyTests.cs"
Task: "T318 Replacement state machine tests in tests/Warranty.UnitTests/Fulfillment/ReplacementStateMachineTests.cs"
Task: "T319 RecordReplacementEvent tests in tests/Warranty.UnitTests/Application/RecordReplacementEventTests.cs"
Task: "T320 Claimant projection tests in tests/Warranty.UnitTests/Fulfillment/ClaimantReplacementProjectionTests.cs"
Task: "T321 Replacement scenario seed in seed/tenants/*/replacement-scenarios.json and ReplacementScenarioSeeder.cs"
Task: "T322 US1 integration tests in tests/Warranty.IntegrationTests/Fulfillment/US1_ReplacementCollectionTests.cs"

# UI mappings alongside the backend:
Task: "T331 Replacement labels in src/web/src/shared/presentation/replacement.ts"
```

---

## Implementation Strategy

### MVP First (User Story 1)

1. Setup → Foundational (migration, seed, RLS checked)
2. US1 → **STOP and VALIDATE** X1–X5 and X18: a same-model replacement from suggested location to
   hand-over, with one notification and a registered serial

US1 alone replaces 002's single "Replacement handed to customer" step with tracked collection.

### Incremental Delivery

| Step | Scope |
|------|-------|
| 1 | Setup + Foundational |
| 2 | US1 (MVP: collection and notification) |
| 3 | US2 (return rules) and US4 (later claims) in parallel |
| 4 | US3 (equivalent model, not available) |
| 5 | US5 (moves, refusals, corrections) |
| 6 | Polish and full validation |

### Parallel Team Strategy

After Foundational and US1: Developer A takes US2 → US3 → US5 (they share `Replacement.cs`,
`RecordReplacementEvent.cs` and the Replacement card), Developer B takes US4.

---

## Notes

- [P] tasks = different files, no dependency on an incomplete task; [USn] maps a task to its story
- One task, one branch, one PR (`scripts/git/task-flow.ps1 start T3xx` / `finish T3xx`); GitHub issues can be created with `/speckit-taskstoissues`
- The EF Core migration (T309) is a shared hot spot: only T309 creates it; later model changes in this feature are folded back into it before it reaches `master`, or a follow-up migration is generated
- No task changes AI prompts, model output schemas or replay recordings (research R11, R14); the evaluation runner is unaffected
- `/speckit-implement` treats unchecked checklist items as a gate; `checklists/requirements.md` passes
