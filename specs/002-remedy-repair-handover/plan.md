# Implementation Plan: Policy-Driven Remedies and Repair-Centre Hand-over

**Branch**: `002-remedy-repair-handover` (spec directory; documents on `docs/002-repair-handover-spec`, implementation on per-task branches, see CLAUDE.md) | **Date**: 2026-10-14 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/002-remedy-repair-handover/spec.md`

## Summary

Give every approved claim a remedy (Repair, Replacement or Refund) taken from its company's own,
versioned remedy table, and coordinate a Repair with the company's external repair centre: suggest
a centre, record the hand-over, readiness, collection and failed or uneconomical repairs as staff
hear them from the centre, re-evaluate the policy after a failure, notify the customer once when
the product is ready, and show progress on the claimant status page. The repair centre's own
software is not connected.

Technical approach: extend the existing .NET 10 modular monolith of feature 001. Remedy rules are
structured policy terms (a `remedies` block next to `terms`, research R1). A pure, deterministic
`RemedyPolicyEvaluator` in `Warranty.Guardrails` decides allowed remedies for all companies alike
(R2). The `ActionExecutor` sets the remedy inside the approval transaction and applies reviewer
remedy decisions and refund requests (R3, R4, R10). A `Repair` aggregate in the domain enforces
the repair state machine; staff updates go through one application command with optimistic
concurrency and an append-only event history (R5, R6). The ready notification is made unique by a
database key (R7). Repair centres are the existing service centres with address, opening hours,
active flag and priority (R8). The protection plan is an intake field and evidence kind, checked by
a reviewer (R9). No AI prompt, model output schema or recording changes.

## Technical Context

**Language/Version**: C# 14 / .NET 10 (backend); TypeScript (strict) / React 19 (frontend) — as 001

**Primary Dependencies**: unchanged from 001 (ASP.NET Core 10 Minimal APIs, EF Core 10 + Npgsql,
.NET Aspire 13, TanStack Query, openapi-typescript / openapi-fetch). No new packages.

**Storage**: PostgreSQL 17, database `warranty`: new tables in the existing `claims` and `audit`
schemas, new columns on `policy.policy_versions`, `integration.service_centers`,
`integration.notifications`, `claims.claims` ([data-model.md](./data-model.md)). One EF Core
migration. Blob storage: protection plan documents in the tenant's existing evidence container.

**Testing**: xUnit v3, Shouldly, NSubstitute, NetArchTest, Testcontainers on Podman,
WebApplicationFactory, replay provider (S1 recordings reused); Vitest + RTL + MSW — as 001.
New integration tests carry the trait `Feature=002`.

**Target Platform**: Local developer workstation with Podman 5, as 001

**Project Type**: Web application — REST API + background worker + SPA (existing)

**Performance Goals**: Staff record a hand-over or status update in under one minute (SC-001);
claim detail with remedy and repair loads in < 2 s like the other staff screens; the claimant sees
the new status on the next refresh (SC-005)

**Constraints**: No change to 001 claim outcomes (SC-009); AI never chooses a remedy (FR-014);
no integration with repair-centre software; no money movement (refund through a port with a
simulated adapter, R10); seeded policy clause text unchanged so replay recordings stay valid (R12)

**Scale/Scope**: 2 seeded tenants; 4 remedy table rows; 5 repair statuses, 9 repair event types;
4 new staff endpoints and 6 changed ones; 1 new staff tab, changes to 3 staff screens and 2
claimant screens

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | How this plan complies | Pre-research | Post-design |
|---|-----------|------------------------|:------------:|:-----------:|
| I | Security-First: Tenant Isolation | New tables carry `tenant_id` and get the existing RLS policy automatically (`001_roles_and_rls.sql` loops over every table with `tenant_id`); composite FKs with `tenant_id` for claim, repair and service-centre references; repair centres listed and accepted only from the caller's tenant; cross-tenant IDs return 404 with `ACCESS_DENIED` (FR-022); RLS probe tests on every new table | PASS | PASS |
| II | AI Does Not Directly Control Critical Operations | Remedies come only from the deterministic evaluator and reviewer decisions; no harness tool or prompt can set or change a remedy; remedy effects (repair creation, refund request) run only in `ActionExecutor` (R3, R4, R10), checked by an architecture test that only the executor calls `IRefundService`. Staff repair updates are human-recorded facts, not AI proposals, so they do not need an `ApprovedAction` (R5) | PASS | PASS |
| III | Agent Decisions Must Be Explainable | Every remedy records the policy version, table row, scenario, attempt count and estimate used (`claims.remedy_decisions`); trail entries for each step; the Remedy tab shows the basis | PASS | PASS |
| IV | Human-in-the-Loop for High-Risk Decisions | A choice between remedies, every remedy change, every failed or uneconomical repair outcome and every protection-plan claim go to a reviewer (FR-011, FR-015, FR-031, FR-032, FR-037); self-review refused (R4) | PASS | PASS |
| V | Tenant-Aware by Design | All new commands and queries run under the existing `ITenantContext`; tenant never in a request body; the evaluator receives the claim's own policy version only | PASS | PASS |
| VI | Model Independence | No AI involvement; no change to the gateway or prompts | PASS | PASS |
| VII | Observable AI | No new AI calls. Human overrides of the policy recommendation are recorded as remedy decisions and trail entries; repair events are traced with OpenTelemetry like other API calls | PASS | PASS |
| VIII | PoC-First Architecture | No new project or package; rules as JSON terms instead of a rules engine; staff-entered repair facts instead of an integration; refund through a port with a simulated adapter; the repair event command is the seam for a later repair-centre integration | PASS | PASS |

Development workflow gate: the plan checks all eight principles. No violation; Complexity
Tracking is empty.

## Architecture

### Where the feature plugs into the 001 flow

```text
 Guardrails → ApprovedAction(FinalizeApproved) ─┐        Reviewer approve (+ remedy, plan confirmed)
                                                ▼                       │
                              ActionExecutor (one transaction) ◀────────┘
                                claim.FinalizeApproved(...)              (001, unchanged)
                                RemedyPolicyEvaluator.Evaluate(rules, scenario)   ← NEW, deterministic
                                ├─ one remedy:  ClaimRemedy InProgress
                                │     Repair → Repair(AwaitingHandover, suggested centre)
                                │     Refund → IRefundService.RequestRefundAsync (simulated)
                                └─ choice / no rule: ClaimRemedy AwaitingDecision → review queue
                                approval notification (001, unchanged)

 Staff (agent/reviewer) ── POST /repair/events ──▶ RecordRepairEvent ──▶ Repair state machine
                                                     ├─ audit.repair_events + trail entry
                                                     ├─ MarkedReady → notification (unique key)
                                                     └─ RepairFailed / NotEconomical
                                                          → evaluator → new attempt, or
                                                            ClaimRemedy AwaitingDecision
 Reviewer ── POST /remedy-decisions ──▶ RecordRemedyDecision ──▶ ActionExecutor.ExecuteRemedyDecisionAsync
 Staff ── POST /remedy-completions ──▶ RecordRemedyCompletion (Replacement handed over / Refund completed)
 Claimant ── GET /api/public/claims/{ref} ──▶ ClaimantClaimView.remedy (sanitized projection)
```

### Component changes

| Component | Change |
|-----------|--------|
| `Warranty.Domain` | `Policies/RemedyRules` (+ validation), `PolicyVersion.RemedyRules`; new `Fulfillment/` folder: `RemedyType`, `RemedyScenario`, `RemedyState`, `ClaimRemedy`, `RemedyDecision`, `RemedyCompletion`, `Repair` (state machine), `RepairHandover`, `RepairAttempt`, `RepairEvent`; `ServiceCenter` fields; `Claim.HasProtectionPlan` / `ProtectionPlanConfirmed`; `EvidenceKind.ProtectionPlanDocument`; `EscalationReason.ProtectionPlanToCheck`; requested item `PROTECTION_PLAN_DOCUMENT`; `TrailStep` additions |
| `Warranty.Guardrails` | `Remedies/RemedyPolicyEvaluator` + `RemedyEvaluation` (R2); protection-plan escalation check in the pipeline (R9) |
| `Warranty.Application` | `Actions/ActionExecutor`: remedy at approval, `ExecuteRemedyDecisionAsync`, refund request (R3, R4, R10); `Fulfillment/`: `RecordRemedyDecision`, `RecordRepairEvent`, `RecordRemedyCompletion`, `RepairCentreQuery`, remedy/repair read models, claimant projection; `Review/RecordReviewDecision` and `ReviewQueueQuery` extended; `Claims/SubmitClaim` + intake validation for the plan; ports `IRefundService`, `IServiceNetwork.ListEligibleAsync`, repositories for the new aggregates |
| `Warranty.Infrastructure` | EF configurations and one migration for the new tables and columns; `xmin` row versions; partial unique indexes (one active hand-over, notification `dedupe_key`); repositories |
| `Warranty.Integrations.Simulated` | `SimulatedServiceNetwork` uses active flag and priority; `SimulatedRefundService` replaces the unused `PaymentStub`; `SimulatedNotificationService` accepts a dedupe key |
| `Warranty.MigrationService` | Seed parsing and validation of `remedies` (against [contracts/remedy-rules.schema.json](./contracts/remedy-rules.schema.json)), extended service centres, `remedy-scenarios.json` |
| `Warranty.Api` | New endpoints `GET /repair-centres`, `POST /remedy-decisions`, `POST /repair/events`, `POST /remedy-completions` (group `Fulfillment`, `CrossTenantGuard`); changed claim detail, review queue, review decision, claim submission and claimant view ([contracts/rest-api.delta.openapi.yaml](./contracts/rest-api.delta.openapi.yaml)) |
| `Warranty.AI.Harness`, `Warranty.AI.Gateway`, `Warranty.Knowledge` | No change. The `create_repair_request` tool name stays in the catalog for history; the executor no longer writes `integration.repair_requests` for new approvals (R8) |
| `src/web` | Remedy tab, review queue remedy items, remedy radio group in the review form, protection plan field, claimant remedy timeline ([contracts/ui-screens.md](./contracts/ui-screens.md)); types regenerated from the merged contract |

### Remedy evaluation (deterministic)

| Input | Source |
|-------|--------|
| `RemedyRules?` | applicable `PolicyVersion` of the claim (001 version-selection rule) |
| scenario | R13: `ProtectionPlanDamage` if the reviewer confirmed a plan, else `CoveredDefect`; later `RepairFailed` / `RepairNotEconomical` from repair events |
| failed attempts, latest estimate | `Repair` |
| claim value | `catalog.products.claim_value` (001 FR-001) |

| Output | Meaning |
|--------|---------|
| `AllowedRemedies` | remedies of the matching row; empty when the row or the rules are missing |
| `SingleRemedy` | set when exactly one remedy is allowed → applied automatically |
| `NeedsReviewer` + `PendingReason` | `POLICY_CHOICE`, `NO_REMEDY_RULE`, `REPAIR_FAILED_LIMIT` (single remedy still needs confirmation, FR-031), `REPAIR_NOT_ECONOMICAL` |
| `ContinueRepair` | `RepairFailed` below the attempt limit, or an estimate at or below the limit |
| `TableRow` | for the trail and the UI |

### Error mapping (consistent with 001)

| Situation | HTTP |
|-----------|------|
| Remedy not in the allowed list, missing reason, invalid times, missing event fields | 400 ValidationProblem naming the field |
| Self-review of a remedy | 403 + `SELF_REVIEW_REFUSED` |
| Other tenant's claim or repair centre, unknown ID | 404 + `ACCESS_DENIED` |
| Event not allowed from the current status; remedy not awaiting a decision; lost race on save | 409 with the current status and allowed actions |
| Missing or stale `If-Match` | 412 |

### Testing strategy

| Layer | What | How |
|-------|------|-----|
| Unit | Evaluator rows and boundaries; repair transition table; remedy rules validation; claimant projection field filtering; `ActionExecutor` remedy paths (single, choice, no rule, refund port called once); `RecordRepairEvent` validations | xUnit v3, no I/O |
| Architecture | Evaluator in Guardrails references no AI project; only `ActionExecutor` calls `IRefundService`; existing rules still pass | NetArchTest |
| Integration | API scenarios R1–R12, R14–R17 (quickstart); notification uniqueness under concurrent requests; RLS probes and append-only checks on new tables; migration and seed validation | Testcontainers, WebApplicationFactory, trait `Feature=002` |
| Scenario (replay) | R13: 001 S1 end to end with the remedy and suggested centre | existing S1 recordings |
| Regression | Full 001 suite; tests asserting the simulated repair request updated to the remedy/repair records (R8) | existing suites |
| Frontend | Remedy radio group, repair actions from `allowedEvents`, error messages, claimant timeline | Vitest + RTL + MSW |

### PoC simplifications and extension points

| PoC simplification | Extension point |
|--------------------|-----------------|
| Staff key in what the repair centre reports | `RecordRepairEvent` command: a repair-centre adapter or webhook can submit the same events with source `System` |
| Refund through a simulated adapter | `IRefundService` → the application's real refund option (R10) |
| Remedy rules seeded in policy front-matter | Policy authoring UI writing new versions |
| One ready notification, simulated | `INotificationService` → real email/SMS adapter |
| Replacement only recorded as handed over | Replacement fulfilment feature behind the `Completed` state |
| Protection plan checked by a reviewer | Plan registry lookup as a deterministic check |

## Project Structure

### Documentation (this feature)

```text
specs/002-remedy-repair-handover/
├── spec.md              # Feature specification (/speckit-specify)
├── plan.md              # This file (/speckit-plan)
├── research.md          # Phase 0: decisions R1–R14
├── data-model.md        # Phase 1: tables, state machines, seed changes
├── quickstart.md        # Phase 1: validation scenarios R1–R18
├── contracts/
│   ├── rest-api.delta.openapi.yaml   # merged into 001's canonical contract by the first API task
│   ├── remedy-rules.schema.json      # policy front-matter `remedies` block
│   └── ui-screens.md                 # binding UI contract for src/web tasks
├── checklists/
│   └── requirements.md
└── tasks.md             # Phase 2 (/speckit-tasks — not created by /speckit-plan)
```

### Source Code (repository root)

Only folders that change are shown; everything else is as in
[001 plan.md](../001-ai-claim-adjudication/plan.md).

```text
src/
├── Warranty.Domain/
│   ├── Policies/RemedyRules.cs                 # NEW
│   ├── Fulfillment/                            # NEW: ClaimRemedy, RemedyDecision, RemedyCompletion,
│   │                                           #      Repair, RepairHandover, RepairAttempt, RepairEvent, enums
│   ├── Integration/IntegrationRecords.cs       # ServiceCenter fields
│   ├── Claims/                                 # protection plan fields, evidence kind, escalation reason
│   └── Common/TrailStep.cs                     # new steps
├── Warranty.Guardrails/
│   ├── Remedies/RemedyPolicyEvaluator.cs       # NEW
│   └── Pipeline/Checks/                        # protection-plan escalation check
├── Warranty.Application/
│   ├── Actions/ActionExecutor.cs               # remedy at approval, remedy decisions, refund request
│   ├── Fulfillment/                            # NEW: RecordRemedyDecision, RecordRepairEvent,
│   │                                           #      RecordRemedyCompletion, queries, claimant projection
│   ├── Review/                                 # review decision + queue extended
│   ├── Claims/                                 # submission + intake validation for the plan
│   └── Abstractions/Integrations/              # IRefundService, IServiceNetwork.ListEligibleAsync
├── Warranty.Infrastructure/Persistence/
│   ├── Configurations/FulfillmentConfigurations.cs   # NEW
│   ├── Repositories/FulfillmentRepositories.cs       # NEW
│   └── Migrations/                             # one new migration
├── Warranty.Integrations.Simulated/            # SimulatedRefundService (replaces PaymentStub), network, notifications
├── Warranty.MigrationService/Seeding/          # remedies, service centres, remedy scenarios
├── Warranty.Api/Endpoints/Fulfillment/         # NEW: FulfillmentEndpoints.cs
└── web/src/
    ├── features/remedy/                        # NEW: RemedyTab, RepairCard, event dialogs, remedy dialog
    ├── features/review/                        # remedy radio group, queue item kind
    ├── features/claimant/                      # protection plan field, remedy timeline
    └── shared/presentation/                    # remedy and repair labels/tones
seed/tenants/{aurora,borealis}/
├── policies/*.md                               # `remedies` front-matter block
├── service-centers.json                        # address, hours, contact, priority, extra centres
└── remedy-scenarios.json                       # NEW
tests/
├── Warranty.UnitTests/Fulfillment/             # NEW
├── Warranty.UnitTests/Guardrails/Remedies/     # NEW
└── Warranty.IntegrationTests/Fulfillment/      # NEW (trait Feature=002)
```

**Structure Decision**: Extend the existing modular monolith; no new project. The remedy evaluator
sits in `Warranty.Guardrails` so the architecture tests that keep Guardrails free of AI cover it.
The fulfilment model gets its own `Fulfillment` folder in Domain, Application, Infrastructure and
Api, mirroring the existing per-domain folders. The canonical REST contract stays in feature 001's
folder because the SPA generates its types from it (R11).

## Complexity Tracking

No constitution violations; nothing to justify.
