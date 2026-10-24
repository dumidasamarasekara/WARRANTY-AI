---

description: "Task list for policy-driven remedies and repair-centre hand-over"
---

# Tasks: Policy-Driven Remedies and Repair-Centre Hand-over

**Input**: Design documents from `/specs/002-remedy-repair-handover/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Tests**: Included. The plan's testing strategy requires unit, integration and frontend tests, and
SC-002 – SC-004, SC-007 – SC-009 need automated verification. Within each story, write the tests
first; following CLAUDE.md, a test task lands its cases with `Skip = "Pending T2xx"` for every
implementing task not yet merged, and the implementing task removes those skips.

**Organization**: Tasks are grouped by user story (US1–US6 of spec.md). Scenario IDs R1–R18 refer
to [quickstart.md §2](./quickstart.md). Research decisions R1–R14 are written "research R*n*".

**Task IDs start at T201** so that branch names (`task/T2xx-…`) and GitHub issue titles
(`T2xx: …`) never collide with feature 001's T001–T128.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on an incomplete task)
- **[Story]**: Which user story the task belongs to (US1–US6)
- Paths follow plan.md: .NET projects in `src/Warranty.*`, SPA in `src/web`, tests in `tests/`,
  seed data in `seed/`
- New integration test classes carry `[Trait("Feature", "002")]`

## Fixed identifiers used across tasks

| Item | Value |
|------|-------|
| Aurora tenant | id `11111111-1111-7111-8111-111111111111`, policy `AUR-WP` v1 and v2 |
| Borealis tenant | id `22222222-2222-7222-8222-222222222222`, policy `BOR-WP` v1 |
| Remedy rules | Aurora: `coveredDefect: Repair`, `repairFailed: {afterFailedAttempts: 2, remedy: Replacement}`, `protectionPlan: {liftsAccidentalDamageExclusion: false}`. Borealis: `coveredDefect: Repair`, `repairNotEconomical: {limitPercentOfClaimValue: 60, allowed: [Refund, Replacement]}`, `repairFailed: {afterFailedAttempts: 2, remedy: Refund}`, `protectionPlan: {liftsAccidentalDamageExclusion: true, remedy: Repair}` |
| Ready notification | template `RepairReadyForCollection`, dedupe key `repair-ready:{attemptId}` |
| Canonical REST contract | `specs/001-ai-claim-adjudication/contracts/rest-api.openapi.yaml` (research R11) |

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Contract and schema in place before code depends on them

- [X] T201 Merge [contracts/rest-api.delta.openapi.yaml](./contracts/rest-api.delta.openapi.yaml) into the canonical `specs/001-ai-claim-adjudication/contracts/rest-api.openapi.yaml`: add the four new paths under a `Fulfillment` tag, fold the `*Additions` schemas into `ClaimDetail`, `ReviewQueueItem`, `ReviewDecisionRequest`, `ClaimSubmissionForm`/`ClaimSubmissionData` and `ClaimantClaimView`, add the new schemas and the new `TrailStep` values; regenerate `src/web/src/shared/api/schema.d.ts` with `npm --prefix src/web run gen:api` and make `npm --prefix src/web run build` pass
- [X] T202 [P] Add the remedy rules schema for seed validation: copy `specs/002-remedy-repair-handover/contracts/remedy-rules.schema.json` to `src/Warranty.MigrationService/Seeding/Schemas/remedy-rules.schema.json` as an embedded resource in `src/Warranty.MigrationService/Warranty.MigrationService.csproj`, and add a `JsonSchema.Net` package reference there (already in `Directory.Packages.props`, used by the AI Gateway and Harness)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Domain types, persistence (one migration), ports, simulated adapters and seed data used by every story

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Domain and ports

- [X] T203 [P] Create `RemedyRules` (rows `CoveredDefect`, `RepairNotEconomical` with `LimitPercentOfClaimValue`, `RepairFailed` with `AfterFailedAttempts`, `ProtectionPlan` with `LiftsAccidentalDamageExclusion`; each row holds either one `Remedy` or an `Allowed` list) with `Validate()` enforcing the rules in data-model.md, in `src/Warranty.Domain/Policies/RemedyRules.cs`; add nullable `RemedyRules` to `PolicyVersion` and its `Create` factory in `src/Warranty.Domain/Policies/PolicyVersion.cs`
- [X] T204 [P] Create the fulfilment enums `RemedyType`, `RemedyScenario`, `RemedyState`, `RemedyPendingReason`, `RemedyDecisionKind`, `RemedyDecidedBy`, `RepairStatus`, `RepairNotPossibleReason`, `RepairEventType`, `ReportSource`, `HandoverAssignment`, `AttemptResult` with the values in data-model.md, in `src/Warranty.Domain/Fulfillment/FulfillmentEnums.cs`
- [X] T205 [P] Create `ClaimRemedy` (state `AwaitingDecision`/`InProgress`/`Completed`, allowed remedies, pending reason; methods `SetByPolicy`, `AwaitDecision`, `Decide`, `Complete` that refuse a remedy outside `AllowedRemedies`), `RemedyDecision` (insert-only history row) and `RemedyCompletion` in `src/Warranty.Domain/Fulfillment/ClaimRemedy.cs`, `RemedyDecision.cs`, `RemedyCompletion.cs`
- [X] T206 [P] Create the shapes of `Repair` (status, active hand-over, current attempt, failed attempts; `Create` in `AwaitingHandover` with an optional suggested centre), `RepairHandover`, `RepairAttempt` and `RepairEvent` per data-model.md in `src/Warranty.Domain/Fulfillment/Repair.cs`, `RepairHandover.cs`, `RepairAttempt.cs`, `RepairEvent.cs` (transition methods come in T237, T246 and T256)
- [X] T207 [P] Extend `ServiceCenter` with `Address`, `OpeningHours`, `Contact`, `IsActive`, `Priority` and the notification record with a nullable `DedupeKey` in `src/Warranty.Domain/Integration/IntegrationRecords.cs`
- [X] T208 [P] Add `HasProtectionPlan` and `ProtectionPlanConfirmed` to `src/Warranty.Domain/Claims/Claim.cs`; `EvidenceKind.ProtectionPlanDocument` in `src/Warranty.Domain/Claims/EvidenceKind.cs`; `EscalationReason.ProtectionPlanToCheck` with the label "protection plan to be checked" in `src/Warranty.Domain/Claims/EscalationReason.cs`; requested item `PROTECTION_PLAN_DOCUMENT` ("protection plan document") in `src/Warranty.Domain/Claims/RequestedItemCodes.cs` and `src/Warranty.Application/Claims/RequestedItemCatalog.cs`; the `TrailStep` values `RemedyRecommended`, `RemedyDecided`, `RepairCentreAssigned`, `RepairHandedOver`, `RepairStatusUpdated`, `RepairAttemptEnded`, `RemedyCompleted` in `src/Warranty.Domain/Common/TrailStep.cs`
- [X] T209 Add ports: `IRefundService.RequestRefundAsync(claimId, amount, ct)`, `IServiceNetwork.ListEligibleAsync(region, productCategory, ct)` (active centres ordered by priority then name, with a `ServesClaim` flag) and an `INotificationService.EnqueueAsync` overload with a dedupe key that reports "already queued" in `src/Warranty.Application/Abstractions/Integrations/IntegrationPorts.cs`; repositories `IRemedyRepository` (get/add `ClaimRemedy`, add-only `RemedyDecision`, add `RemedyCompletion`) and `IRepairRepository` (get by claim with hand-overs and attempts, add, add-only `RepairEvent`) in `src/Warranty.Application/Abstractions/Persistence/Repositories.cs`

### Persistence, adapters and seed

- [X] T210 Add EF Core configurations for `claim_remedies`, `remedy_decisions`, `remedy_completions`, `repairs`, `repair_handovers`, `repair_attempts` (schema `claims`) and `repair_events` (schema `audit`) with composite tenant FKs, `xmin` row versions on `claim_remedies` and `repairs`, the partial unique index "one active hand-over per repair", in `src/Warranty.Infrastructure/Persistence/Configurations/FulfillmentConfigurations.cs`; extend the existing configurations for `policy_versions.remedy_rules` (jsonb), the service-centre columns, `notifications.dedupe_key` (unique `(tenant_id, dedupe_key)` where not null) and the claim columns; add the `DbSet`s in `src/Warranty.Infrastructure/Persistence/WarrantyDbContext.cs`
- [X] T211 Generate the migration `AddRemediesAndRepairs` in `src/Warranty.Infrastructure/Persistence/Migrations/` (`dotnet ef migrations add`, one migration for the whole feature; if `finish` reports a model-snapshot conflict, drop and regenerate it as CLAUDE.md says) and confirm the existing `001_roles_and_rls.sql` loop gives every new table RLS and `audit.repair_events` the append-only trigger without SQL changes
- [X] T212 Implement `IRemedyRepository` and `IRepairRepository` in `src/Warranty.Infrastructure/Persistence/Repositories/FulfillmentRepositories.cs` (no update or delete for `RemedyDecision` and `RepairEvent`), translate a dedupe-key unique violation into "already queued" in `src/Warranty.Infrastructure/Persistence/Repositories/IntegrationRepositories.cs`, and register both in the Infrastructure DI
- [X] T213 [P] Update `src/Warranty.Integrations.Simulated/`: `SimulatedServiceNetwork.cs` honours `IsActive` and `Priority` and implements `ListEligibleAsync`; new `SimulatedRefundService.cs` records a simulated refund request and moves no money; delete the unused `PaymentStub.cs`; `SimulatedNotificationService.cs` passes the dedupe key; register in `DependencyInjection.cs`
- [X] T214 Read and validate the `remedies` front-matter block (embedded schema from T202, then `RemedyRules.Validate`) and the new service-centre fields in `src/Warranty.MigrationService/Seeding/SeedFiles.cs` and `src/Warranty.MigrationService/Seeding/TenantSeeder.cs`; a version without `remedies` seeds `RemedyRules = null`
- [X] T215 [P] Add the `remedies` blocks from "Fixed identifiers" to `seed/tenants/aurora/policies/AUR-WP-v1.md`, `seed/tenants/aurora/policies/AUR-WP-v2.md` and `seed/tenants/borealis/policies/BOR-WP-v1.md` without changing any clause text or key (research R12); extend `seed/tenants/aurora/service-centers.json` and `seed/tenants/borealis/service-centers.json` with `address`, `openingHours`, `contact`, `priority`, and add one active centre for Aurora NA, Aurora EU and Borealis EU so every region has at least two (FR-045)

### Foundational tests

- [X] T216 [P] Write `tests/Warranty.UnitTests/Domain/RemedyRulesTests.cs` (valid examples from the schema; `remedy` and `allowed` together, a one-item `allowed`, limit 0 or 101, attempts 0 or 6, plan lifting without remedy all throw) and update `tests/Warranty.UnitTests/Seed/SeedFilesTests.cs` and `tests/Warranty.UnitTests/Seed/SeedDataTests.cs` for the remedy rules and repair centres
- [X] T217 [P] Extend `tests/Warranty.IntegrationTests/Isolation/RowLevelSecurityTests.cs` with raw-SQL probes as the other tenant on every new table, add an append-only check for `audit.repair_events` (UPDATE and DELETE rejected), and extend `tests/Warranty.IntegrationTests/Migration/MigrationServiceTests.cs` to assert seeded remedy rules per version and at least two active centres per region
- [X] T218 [P] Add an architecture rule that only `ActionExecutor` depends on `IRefundService` in `tests/Warranty.UnitTests/Architecture/DependencyRulesTests.cs`

**Checkpoint**: migration applies, seed loads remedy rules and centres, RLS covers the new tables

---

## Phase 3: User Story 1 - Remedy decided by the company's policy (Priority: P1) 🎯 MVP

**Goal**: Every approved claim gets a remedy from its own company's remedy table; a choice goes to a reviewer; disallowed remedies are refused

**Independent Test**: quickstart R1–R3 and R13: Aurora and Borealis covered defects get Repair set by policy with the policy version and row recorded; a reviewer cannot pick Refund for an Aurora defect; 001 S1 still auto-approves and now gets a remedy and a suggested centre

### Tests for User Story 1 ⚠️ write first, ensure they fail

- [X] T219 [P] [US1] Write `tests/Warranty.UnitTests/Guardrails/Remedies/RemedyPolicyEvaluatorTests.cs`: every row of both seeded rule sets; `allowed` lists need a reviewer and give no recommendation; missing rules or row → `NO_REMEDY_RULE`; `RepairFailed` at 1, 2 and 3 attempts with limit 2; estimate at 59%, exactly 60% (economical) and 60.01%; protection plan allowed and not allowed; identical inputs give identical outputs
- [X] T220 [P] [US1] Write `tests/Warranty.UnitTests/Application/ActionExecutorRemedyTests.cs`: automatic and reviewer approvals set the policy remedy in the same transaction; a choice leaves the claim `Approved` with remedy `AwaitingDecision`; Repair creates a `Repair` with the suggested centre and no `repair_requests` row; Refund calls `IRefundService` exactly once; trail entries `RemedyRecommended`, `RemedyDecided`, `RepairCentreAssigned` with policy version and row
- [X] T221 [P] [US1] Write `tests/Warranty.UnitTests/Application/RecordRemedyDecisionTests.cs` and extend `tests/Warranty.UnitTests/Application/RecordReviewDecisionTests.cs`: remedy outside the allowed list → 400 naming the allowed remedies; missing reason for a change → 400; self-review → 403 with `SELF_REVIEW_REFUSED`; stale `If-Match` → 412; remedy not awaiting a decision → 409; Approve without remedy only when the preview gives one remedy
- [X] T222 [P] [US1] Create `seed/tenants/aurora/remedy-scenarios.json` and `seed/tenants/borealis/remedy-scenarios.json` with the approved claims for R1 and R2 (and placeholders for the R4 pair used in T245), loaded by a new `src/Warranty.MigrationService/Seeding/RemedyScenarioSeeder.cs` that creates the approved claims, their trail and their remedy through the same domain methods as the executor (marked as seeded history like `historical-claims.json`)
- [X] T223 [P] [US1] Write `tests/Warranty.IntegrationTests/Fulfillment/US1_RemedyDecisionTests.cs` for R1, R2, R3 and R13 (R13 on the existing S1 recordings: outcome exactly as in 001, then remedy Repair and a suggested centre, no `integration.repair_requests` row)

### Implementation for User Story 1

- [X] T224 [US1] Implement `RemedyPolicyEvaluator` and `RemedyEvaluation` (inputs and outputs as in plan.md "Remedy evaluation") in `src/Warranty.Guardrails/Remedies/RemedyPolicyEvaluator.cs` and `src/Warranty.Guardrails/Remedies/RemedyEvaluation.cs`; register it in `src/Warranty.Guardrails/DependencyInjection.cs`
- [ ] T225 [US1] In `src/Warranty.Application/Actions/ActionExecutor.cs`, after `FinalizeApproved` (system and reviewer paths) evaluate the remedy with the claim's applicable policy version (scenario per research R13) and write `ClaimRemedy` + `RemedyDecision` rows; for Repair create the `Repair` with the first `ListEligibleAsync` centre (`CentreSuggested` event) instead of calling `CreateRepairRequestAsync`; for Refund call `IRefundService`; keep the 001 approval notification; append the new trail steps
- [ ] T226 [US1] Update the 001 tests that assert an `integration.repair_requests` row or the "Simulated repair request created" trail text to assert the remedy and repair records instead, without changing any outcome assertion (research R8): `tests/Warranty.UnitTests/Application/ActionExecutorTests.cs`, `tests/Warranty.UnitTests/Application/RecordReviewDecisionTests.cs`, `tests/Warranty.UnitTests/Integrations/SimulatedIntegrationTests.cs`, `tests/Warranty.UnitTests/Harness/Tools/ConsequentialToolTests.cs`, `tests/Warranty.IntegrationTests/Scenarios/GoldenScenario.cs`, `tests/Warranty.IntegrationTests/Scenarios/US1_AutomatedAdjudicationTests.cs` – `US5_RequestInformationTests.cs`, `tests/Warranty.IntegrationTests/Smoke/AppHostSmokeTests.cs`
- [ ] T227 [US1] Add `ExecuteRemedyDecisionAsync` to `src/Warranty.Application/Actions/IActionExecutor.cs` and `ActionExecutor.cs` (tenant check, self-review guard, re-evaluation and allowed-list check, reason rule, Repair or refund side effects, trail `RemedyDecided`) and the use case `src/Warranty.Application/Fulfillment/RecordRemedyDecision.cs` with results mirroring `RecordReviewDecisionResult`
- [ ] T228 [US1] Extend `src/Warranty.Application/Review/RecordReviewDecision.cs` and the executor's reviewer path with `remedy` and `protectionPlanConfirmed` (validation per the contract; `protectionPlanConfirmed` is stored and used from T262 on), and `src/Warranty.Application/Review/ReviewQueueQuery.cs` with `RemedyDecision` items and `remedyPendingReason`
- [X] T229 [US1] Create the read models `RemedyView` and `RemedyPreview` (same evaluator, research R2) in `src/Warranty.Application/Fulfillment/RemedyQueries.cs` and add `remedy`, `remedyPreview` and `hasProtectionPlan` to the claim detail in `src/Warranty.Application/Claims/ClaimQueries.cs`
- [ ] T230 [US1] Create `src/Warranty.Api/Endpoints/Fulfillment/FulfillmentEndpoints.cs` with `POST /api/claims/{claimId}/remedy-decisions` (role claims-reviewer, `If-Match`, error mapping in plan.md), register the group with `CrossTenantGuard` in `src/Warranty.Api/Endpoints/EndpointGroups.cs`, and map the new request/response fields in `src/Warranty.Api/Endpoints/Review/ReviewEndpoints.cs` and `src/Warranty.Api/Endpoints/Claims/ClaimEndpoints.cs`
- [X] T231 [P] [US1] Add the remedy and repair labels and tones of [contracts/ui-screens.md §1](./contracts/ui-screens.md) in `src/web/src/shared/presentation/remedy.ts`, and labels for the new trail steps in the trace presentation used by `src/web/src/features/trace/decisionTrace.ts`, with tests next to them
- [X] T232 [US1] Build the staff Remedy tab (remedy card with origin line, policy basis, history timeline, `warn` banner and **Decide remedy** dialog for reviewers) in `src/web/src/features/remedy/RemedyTab.tsx`, `RemedyDialog.tsx`, `remedyQueries.ts`; add the route `/staff/claims/:claimId/remedy` in `src/web/src/app/routes.tsx` and the claim header's Remedy progress step; Vitest tests that the dialog lists only allowed remedies and sends `If-Match`
- [X] T233 [US1] Add the remedy radio group to `src/web/src/features/review/ReviewDecisionForm.tsx` and `reviewDecision.ts` (only `remedyPreview.allowedRemedies`, pre-selected recommendation, submit blocked until chosen) and `RemedyDecision` items to `src/web/src/features/review/ReviewQueuePage.tsx`; Vitest tests

**Checkpoint**: US1 delivers policy-grounded remedies on every new approval; validate R1–R3 and R13

---

## Phase 4: User Story 2 - Repair hand-over, readiness and customer notification (Priority: P1)

**Goal**: Staff record the hand-over, readiness and collection reported by the repair centre; the customer is notified once and sees progress

**Independent Test**: quickstart R5–R7, R16, R17: hand-over → ready → collected on an Aurora claim, one notification under double clicks and concurrent staff, refused out-of-order steps, no cross-tenant centres, read-only auditor

### Tests for User Story 2 ⚠️ write first, ensure they fail

- [X] T234 [P] [US2] Write `tests/Warranty.UnitTests/Fulfillment/RepairStateMachineTests.cs`: every pair of status × event of the data-model.md table is allowed or refused as listed (US3/US5 events skipped as `Pending T246`/`Pending T256`); `allowedEvents` per status; `Collected` is final
- [X] T235 [P] [US2] Write `tests/Warranty.UnitTests/Application/RecordRepairEventTests.cs`: `reportedAt` in the future or before the hand-over → 400; missing event fields → 400; other tenant's centre → not found + `ACCESS_DENIED`; stale `If-Match` → 412; refused transition → 409 naming status and allowed events; MarkedReady queues one notification with key `repair-ready:{attemptId}`; Collected completes the remedy
- [X] T236 [P] [US2] Write `tests/Warranty.UnitTests/Fulfillment/ClaimantRemedyProjectionTests.cs`: labels of data-model.md "Claimant projection" for each state; the projection never contains notes, centre references, failure reasons, contact details or staff names
- [X] T237 [P] [US2] Write `tests/Warranty.IntegrationTests/Fulfillment/US2_RepairHandoverTests.cs` for R5, R6, R7 (parallel requests prove one status change and one notification row), R16 and R17

### Implementation for User Story 2

- [X] T238 [US2] Implement `ChangeCentre`, `RecordHandover`, `MarkReady`, `RecordCollection` and `AllowedEvents` on `Repair` (opening attempt 1 on the first hand-over, emitting `RepairEvent`s) in `src/Warranty.Domain/Fulfillment/Repair.cs` and `RepairAttempt.cs`
- [X] T239 [US2] Implement `src/Warranty.Application/Fulfillment/RecordRepairEvent.cs` for `CentreChanged`, `HandedOver`, `MarkedReady`, `Collected`: roles claims-agent and claims-reviewer, `If-Match` on the repair, centre eligibility through `IServiceNetwork` (same tenant, active), one transaction writing the repair, `audit.repair_events` and a trail entry, the ready notification with its dedupe key, and remedy `Completed` on `Collected`
- [ ] T240 [US2] Create `RepairView` (with `allowedEvents`, events with staff display names, economic limit amount) and the eligible-centre query in `src/Warranty.Application/Fulfillment/RepairQueries.cs`; add `repair` to the claim detail in `src/Warranty.Application/Claims/ClaimQueries.cs`
- [ ] T241 [US2] Add `GET /api/claims/{claimId}/repair-centres` (any staff role) and `POST /api/claims/{claimId}/repair/events` (claims-agent, claims-reviewer) to `src/Warranty.Api/Endpoints/Fulfillment/FulfillmentEndpoints.cs` with the error mapping of plan.md
- [X] T242 [US2] Implement the claimant projection in `src/Warranty.Application/Fulfillment/ClaimantRemedyProjection.cs` and add `remedy` to the claimant view in `src/Warranty.Application/Claims/ClaimantAccess.cs` (or the claimant query it uses) and `src/Warranty.Api/Endpoints/Public/ClaimantEndpoints.cs`
- [X] T243 [US2] Build the Repair card, the event dialogs for Record hand-over, Change centre, Mark ready and Record collection, and the repair events timeline in `src/web/src/features/remedy/RepairCard.tsx`, `RepairEventDialog.tsx`, `RepairEventsTimeline.tsx`; buttons only from `allowedEvents`; 409 and 412 messages from [contracts/ui-screens.md §2](./contracts/ui-screens.md); auditors read-only; Vitest tests
- [X] T244 [US2] Replace "The service centre will contact you" with the remedy section (remedy line, four-step timeline, centre card, ready callout) in `src/web/src/features/claimant/ClaimStatusPage.tsx` and `src/web/src/features/claimant/claimProgress.ts`; Vitest tests including the absence of staff-only fields

**Checkpoint**: US1 + US2 give the full happy path from approval to collection; validate R5–R7, R16, R17

---

## Phase 5: User Story 3 - Failed or uneconomical repair and policy re-evaluation (Priority: P2)

**Goal**: Failed attempts are counted, estimates are checked against the economic limit, and the policy's next remedy is recommended for a reviewer to confirm

**Independent Test**: quickstart R4, R8–R10: Aurora's second failure recommends Replacement, Borealis's recommends Refund, a Borealis estimate above 60% requires a reviewer choice between Refund and Replacement

### Tests for User Story 3 ⚠️ write first, ensure they fail

- [ ] T245 [P] [US3] Write `tests/Warranty.UnitTests/Fulfillment/RepairFailureTests.cs` (failure counting, not-economical never counted, new attempt at the centre or after return, re-evaluation results for both rule sets) and unskip the US3 cases of `tests/Warranty.UnitTests/Fulfillment/RepairStateMachineTests.cs`; complete the R4 "same facts" pair in `seed/tenants/*/remedy-scenarios.json`
- [ ] T246 [P] [US3] Write `tests/Warranty.IntegrationTests/Fulfillment/US3_FailedRepairTests.cs` for R4, R8, R9 and R10 (claim status stays `Approved` throughout; history keeps the original remedy and every attempt)

### Implementation for User Story 3

- [ ] T247 [US3] Implement `RecordEstimate`, `RecordFailure` and `StartNewAttempt` on `Repair` and `RepairAttempt` (failed attempts counted only for `Failed`; `NotEconomical` only when the version has a limit and the estimate is strictly above it) in `src/Warranty.Domain/Fulfillment/Repair.cs` and `RepairAttempt.cs`
- [ ] T248 [US3] Extend `src/Warranty.Application/Fulfillment/RecordRepairEvent.cs` with `EstimateReported`, `RepairFailed`, `NewAttemptStarted`: after a failure or an uneconomical estimate, run `RemedyPolicyEvaluator` and either allow a new attempt or move `ClaimRemedy` to `AwaitingDecision` with `REPAIR_FAILED_LIMIT` or `REPAIR_NOT_ECONOMICAL`, writing an `Evaluated` remedy decision and the trail step `RepairAttemptEnded`
- [ ] T249 [US3] Let `ExecuteRemedyDecisionAsync` in `src/Warranty.Application/Actions/ActionExecutor.cs` confirm a remedy after a failure (a single recommended remedy still needs the reviewer, FR-031), close the repair, and request the refund or record the replacement; queue labels for the new pending reasons in `src/Warranty.Application/Review/ReviewQueueQuery.cs`
- [ ] T250 [US3] Add the Record estimate, Repair failed and Start new attempt dialogs, the "failed attempts 1 of 2" and estimate-versus-limit display in `src/web/src/features/remedy/RepairCard.tsx` and `RepairEventDialog.tsx`, and the "Repair not possible. Your claim is being reviewed." banner on `src/web/src/features/claimant/ClaimStatusPage.tsx`; Vitest tests

**Checkpoint**: company-specific failure rules demonstrated; validate R4, R8–R10

---

## Phase 6: User Story 4 - Replacement and refund completion (Priority: P2)

**Goal**: Replacement and Refund remedies get a recorded completion visible to staff and the customer

**Independent Test**: quickstart R11 on seeded claims whose remedy is already Replacement and Refund

### Tests for User Story 4 ⚠️ write first, ensure they fail

- [X] T251 [P] [US4] Write `tests/Warranty.UnitTests/Application/RecordRemedyCompletionTests.cs` (remedy must match and be `InProgress`; Refund needs a reference; date not in the future; Repair refused) and `tests/Warranty.IntegrationTests/Fulfillment/US4_RemedyCompletionTests.cs` for R11, adding claims with decided Replacement and Refund remedies to `seed/tenants/*/remedy-scenarios.json`

### Implementation for User Story 4

- [ ] T252 [US4] Implement `src/Warranty.Application/Fulfillment/RecordRemedyCompletion.cs` (roles claims-agent and claims-reviewer, `If-Match` on the remedy, `RemedyCompletion` row, remedy `Completed`, trail `RemedyCompleted`) and `POST /api/claims/{claimId}/remedy-completions` in `src/Warranty.Api/Endpoints/Fulfillment/FulfillmentEndpoints.cs`
- [ ] T253 [US4] Build the completion card in `src/web/src/features/remedy/CompletionCard.tsx` and the Replacement and Refund labels on `src/web/src/features/claimant/ClaimStatusPage.tsx`; Vitest tests

**Checkpoint**: every remedy type reaches `Completed`; validate R11

---

## Phase 7: User Story 5 - Repair-centre transfer and corrections (Priority: P3)

**Goal**: Transfers and corrections are recorded without overwriting history

**Independent Test**: quickstart R12 and a cross-tenant transfer attempt

### Tests for User Story 5 ⚠️ write first, ensure they fail

- [ ] T254 [P] [US5] Write `tests/Warranty.UnitTests/Fulfillment/RepairTransferCorrectionTests.cs` (one active hand-over after a transfer, attempt count unchanged, correction keeps the original event, no second ready notification for a corrected attempt) and unskip the US5 cases of `RepairStateMachineTests.cs`
- [ ] T255 [P] [US5] Write `tests/Warranty.IntegrationTests/Fulfillment/US5_TransferCorrectionTests.cs` for R12 and a transfer to a Borealis centre from an Aurora claim (404 + `ACCESS_DENIED`)

### Implementation for User Story 5

- [ ] T256 [US5] Implement `Transfer` and `Correct` on `Repair` in `src/Warranty.Domain/Fulfillment/Repair.cs` and the `Transferred` and `Correction` events in `src/Warranty.Application/Fulfillment/RecordRepairEvent.cs` (reason required; correction references `correctsSeq`; corrected values applied; original event untouched)
- [ ] T257 [US5] Add the Transfer and Correct an entry dialogs and the struck-through corrected entries in `src/web/src/features/remedy/RepairEventDialog.tsx` and `RepairEventsTimeline.tsx`; the claimant page shows the new centre; Vitest tests

**Checkpoint**: validate R12

---

## Phase 8: User Story 6 - Protection plan cover (Priority: P3)

**Goal**: A confirmed protection plan lifts the accidental-damage exclusion where the company's policy allows it

**Independent Test**: quickstart R14 and R15

### Tests for User Story 6 ⚠️ write first, ensure they fail

- [X] T258 [P] [US6] Write `tests/Warranty.UnitTests/Guardrails/ProtectionPlanCheckTests.cs` (escalation only when the plan is stated, the damage is accidental or beyond the allowance, and the version lets a plan lift the exclusion; no effect otherwise) and an intake validation test that a stated plan without a document requests `PROTECTION_PLAN_DOCUMENT`
- [ ] T259 [P] [US6] Write `tests/Warranty.IntegrationTests/Fulfillment/US6_ProtectionPlanTests.cs` for R14 and R15: reuse the S4 recordings with a Borealis serial that already used its accidental-damage incident (add it to `seed/tenants/borealis/historical-claims.json` and map the serial in `seed/golden/scenarios.json`) and the Aurora S4 variant with a plan

### Implementation for User Story 6

- [ ] T260 [US6] Accept `hasProtectionPlan` and `protectionPlanDocument` in `src/Warranty.Api/Endpoints/Claims/ClaimSubmissionRequest.cs`, `src/Warranty.Application/Claims/SubmitClaim.cs`, `src/Warranty.Application/Claims/EvidenceUploads.cs` (same sanitizing as invoices) and in supplements (`src/Warranty.Application/Claims/SupplementClaim.cs`); request the document as missing information in the deterministic intake validation step of `src/Warranty.AI.Harness/Agents/IntakeAgent.cs` (no prompt change)
- [ ] T261 [US6] Add the protection-plan escalation check (reason `PROTECTION_PLAN_TO_CHECK`, disposition `HumanReview`) to the guardrail pipeline in `src/Warranty.Guardrails/Pipeline/Checks/` and its registration in `src/Warranty.Guardrails/GuardrailEngine.cs`
- [ ] T262 [US6] Use `protectionPlanConfirmed` in the reviewer approval path (`src/Warranty.Application/Review/RecordReviewDecision.cs`, `src/Warranty.Application/Actions/ActionExecutor.cs`: scenario `ProtectionPlanDamage`) and add `remedyPreviewWithProtectionPlan` in `src/Warranty.Application/Fulfillment/RemedyQueries.cs`
- [ ] T263 [US6] Add the "I have an additional protection plan" checkbox and plan `FileDrop` to `src/web/src/features/claimant/ClaimForm.tsx` and `claimSubmission.ts` (also used by the staff new-claim page), the plan document to `src/web/src/features/claimant/SupplementForm.tsx`, and the "Protection plan checked" checkbox to `src/web/src/features/review/ReviewDecisionForm.tsx`; Vitest tests

**Checkpoint**: validate R14, R15

---

## Phase 9: Polish & Cross-Cutting Concerns

- [ ] T264 [P] Update documentation: add feature 002 to `CLAUDE.md` (what it adds, the `Feature=002` test trait, the T2xx numbering); note in `specs/001-ai-claim-adjudication/spec.md` Assumptions, `plan.md` workflow step 7 and `ui-design.md` §2 that the simulated repair request and "the service centre will contact you" are superseded by feature 002
- [ ] T265 Run the full validation: `dotnet build Warranty.slnx`, `dotnet test --solution Warranty.slnx -- --filter-not-trait "Category=Smoke"`, the frontend lint/test/build, quickstart R1–R18 by hand, and the timed walkthroughs for SC-001 (hand-over and status update under one minute) and SC-006 (auditor explains a remedy from the trail in under 3 minutes); record the results in the PR description

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (T201–T202)**: no dependencies; T201 first so the SPA types exist for every web task
- **Foundational (T203–T218)**: after Setup; blocks every story. T203–T208 in parallel; T209 after them; T210 → T211 → T212; T213–T215 after T209; tests T216–T218 alongside
- **US1 (T219–T233)**: after Foundational
- **US2 (T234–T244)**: after US1 (a Repair exists only once a remedy is set)
- **US3 (T245–T250)**: after US2
- **US4 (T251–T253)**: after US1 (uses seeded Replacement and Refund remedies); independent of US2/US3
- **US5 (T254–T257)**: after US2
- **US6 (T258–T263)**: after US1
- **Polish (T264–T265)**: after every story in scope

### User Story Dependencies (completion order)

```text
Setup → Foundational → US1 ─┬─► US2 ─┬─► US3
                            │        └─► US5
                            ├─► US4
                            └─► US6
```

### Within Each User Story

- Tests and seed fixtures first, landed with `Skip = "Pending T2xx"`; the implementing task unskips them
- Domain transitions before application commands; commands before endpoints; endpoints before the UI that calls them
- `RecordRepairEvent.cs` and `Repair.cs` are touched by US2, US3 and US5 in turn: never run those tasks in parallel

### Parallel Opportunities

- Setup: T202 alongside T201
- Foundational: T203–T208 together; T213, T214, T215 together after T209; T216–T218 alongside T210–T215
- US1: T219–T223 together; T231 alongside T224–T230; T232 and T233 after T229/T230
- US2: T234–T237 together; T243 and T244 after T240–T242
- After US1: US2 (then US3 and US5), US4 and US6 can go to different developers

---

## Parallel Example: User Story 1

```text
# Tests and fixtures together:
Task: "T219 RemedyPolicyEvaluator tests in tests/Warranty.UnitTests/Guardrails/Remedies/RemedyPolicyEvaluatorTests.cs"
Task: "T220 ActionExecutor remedy tests in tests/Warranty.UnitTests/Application/ActionExecutorRemedyTests.cs"
Task: "T221 Remedy decision tests in tests/Warranty.UnitTests/Application/RecordRemedyDecisionTests.cs"
Task: "T222 Remedy scenario seed in seed/tenants/*/remedy-scenarios.json and RemedyScenarioSeeder.cs"
Task: "T223 US1 integration tests in tests/Warranty.IntegrationTests/Fulfillment/US1_RemedyDecisionTests.cs"

# UI mappings alongside the backend:
Task: "T231 Remedy labels in src/web/src/shared/presentation/remedy.ts"
```

---

## Implementation Strategy

### MVP First (User Story 1, then User Story 2)

1. Setup → Foundational (migration, seed, RLS checked)
2. US1 → **STOP and VALIDATE** R1–R3 and R13: every new approval carries a policy remedy
3. US2 → **STOP and VALIDATE** R5–R7: the coordination the business asked for (hand-over, ready, notify, collected)

US1 alone already shows company-specific remedies; US1 + US2 is the smallest release that removes
the manual repair-centre follow-up.

### Incremental Delivery

| Step | Scope |
|------|-------|
| 1 | Setup + Foundational |
| 2 | US1 (MVP: remedies) |
| 3 | US2 (hand-over and readiness) |
| 4 | US3 (failure rules) + US4 (completions) in parallel |
| 5 | US5 (transfers, corrections) + US6 (protection plan) in parallel |
| 6 | Polish and full validation |

### Parallel Team Strategy

After Foundational and US1: Developer A takes US2 → US3 → US5 (they share `Repair.cs` and
`RecordRepairEvent.cs`), Developer B takes US4 and US6.

---

## Notes

- [P] tasks = different files, no dependency on an incomplete task; [USn] maps a task to its story
- One task, one branch, one PR (`scripts/git/task-flow.ps1 start T2xx` / `finish T2xx`); GitHub issues can be created with `/speckit-taskstoissues`
- The EF Core migration (T211) is a shared hot spot: only T211 creates it; later model changes in this feature are folded back into it before it reaches `master`, or a follow-up migration is generated
- No task changes AI prompts, model output schemas or replay recordings (research R9, R12); the evaluation runner is unaffected
- The refund adapter (T213) is simulated; binding the application's real refund option is open (research R10) and needs confirmation before T213 starts
- `/speckit-implement` treats unchecked checklist items as a gate; `checklists/requirements.md` passes
