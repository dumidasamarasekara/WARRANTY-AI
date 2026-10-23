# Implementation Plan: Replacement Fulfilment and Faulty-Product Return

**Branch**: `003-replacement-fulfilment` (spec directory; documents on `docs/003-replacement-spec`, implementation on per-task branches, see CLAUDE.md) | **Date**: 2026-10-23 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/003-replacement-fulfilment/spec.md`

## Summary

Carry out a claim's Replacement remedy the way 002 carries out a Repair: suggest a replacement
location of the same company, take the faulty product back when the company's policy says so
(at collection or before reservation), record the reserved unit's model and serial, notify the
customer once when it is ready, record the hand-over to the customer, and register the new serial
so later claims on it are recognised. A different model needs a reviewer; when no allowed unit is
available the policy is evaluated again ("replacement not available" → Refund at both seeded
companies) and a reviewer confirms. The location's own software is not connected.

Technical approach: build on the 002 design (remedy table, `RemedyPolicyEvaluator`,
`ActionExecutor` remedy paths, service centres, notification dedupe key). Replacement rules extend
the 002 `remedies` block (research R1). A pure `ReplacementPolicy` in `Warranty.Guardrails` checks
models and the initial status (R2). The `ActionExecutor` creates a `Replacement` aggregate when the
remedy becomes Replacement (R3) and cancels it on a remedy change (R13). Staff and reviewer updates
go through one `RecordReplacementEvent` command with optimistic concurrency and an append-only
event stream (R4–R6). A `catalog.replacement_serials` registry feeds the existing deterministic
intake signals, coverage calculator and invoice check for later claims (R10, R11). No AI prompt,
model output schema or recording changes.

## Technical Context

**Language/Version**: C# 14 / .NET 10 (backend); TypeScript (strict) / React 19 (frontend) — as 001

**Primary Dependencies**: unchanged from 001/002. No new packages.

**Storage**: PostgreSQL 17, database `warranty`: new tables in `claims`, `catalog` and `audit`; new
column `integration.service_centers.issues_replacements`; new enum values on 002 columns
([data-model.md](./data-model.md)). One EF Core migration, generated after 002's migration.

**Testing**: as 002 (xUnit v3, Shouldly, NSubstitute, NetArchTest, Testcontainers on Podman,
WebApplicationFactory; Vitest + RTL + MSW). New integration tests carry the trait `Feature=003`.

**Target Platform**: Local developer workstation with Podman 5, as 001

**Project Type**: Web application — REST API + background worker + SPA (existing)

**Performance Goals**: Staff record a step in under one minute (SC-001); claim detail with remedy,
repair and replacement loads in < 2 s; claimant sees the new status on the next refresh (SC-006)

**Constraints**: Feature 002 must be implemented first (its remedy, repair and service-centre
changes are the base); no change to 001/002 outcomes except 002 FR-034 (SC-011); AI never
proposes, approves or chooses a model or a remedy (FR-024, 002 FR-014); no stock, ordering,
shipping or location-software integration; seeded clause text unchanged (002 R12)

**Scale/Scope**: 2 seeded tenants; 2 new policy members; 6 replacement statuses, 12 staff event
types; 3 new staff endpoints and 5 changed ones; changes to the Remedy tab, the review queue and the
claimant status page

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | How this plan complies | Pre-research | Post-design |
|---|-----------|------------------------|:------------:|:-----------:|
| I | Security-First: Tenant Isolation | New tables carry `tenant_id` and get the existing RLS policy; composite FKs with `tenant_id` for claim, location, product and replacement references; locations, models and serials only from the caller's tenant; cross-tenant IDs return 404 with `ACCESS_DENIED` (FR-011); the replacement-serial lookup for later claims is tenant-scoped (001 FR-018); RLS probe tests on every new table | PASS | PASS |
| II | AI Does Not Directly Control Critical Operations | Remedy changes stay in the deterministic evaluator and reviewer decisions through the `ActionExecutor` (002 R3, R4); replacement creation and cancellation happen only there (R3, R13); model approval is a reviewer event; no harness tool or prompt can touch a replacement | PASS | PASS |
| III | Agent Decisions Must Be Explainable | Every step is an event with actor, source and both times plus a trail entry; the Replacement card shows the policy rules and version applied; the new risk signal states why a later claim was flagged | PASS | PASS |
| IV | Human-in-the-Loop for High-Risk Decisions | A different model, a returned-product mismatch and every "not available" remedy change need a reviewer (FR-024, FR-019, FR-026); self-review refused (R5); a claim for a returned serial always goes to human review (FR-030) | PASS | PASS |
| V | Tenant-Aware by Design | All commands and queries run under `ITenantContext`; tenant never in a request body; rules come from the claim's own policy version | PASS | PASS |
| VI | Model Independence | No AI involvement; no gateway or prompt change | PASS | PASS |
| VII | Observable AI | No new AI calls; the new deterministic risk signal is recorded like the other intake signals; replacement endpoints are traced like other API calls | PASS | PASS |
| VIII | PoC-First Architecture | No new project or package; rules as JSON policy terms; staff-entered facts instead of an integration; collection only; the replacement event command is the seam for a later location integration | PASS | PASS |

No violation; Complexity Tracking is empty.

## Architecture

### Where the feature plugs into the 001/002 flow

```text
 ActionExecutor (002: remedy at approval / reviewer remedy decision)
   remedy becomes Replacement ──▶ ReplacementPolicy.InitialStatus(rules, heldByRepairCentre)   ← NEW
                                  Replacement(AwaitingReturn | AwaitingUnit, suggested location)
                                  [held by repair centre → ReturnRecorded (System)]            (R9)
   remedy changes away from Replacement ──▶ Replacement Cancelled                              (R13)

 Staff/Reviewer ── POST /replacement/events ──▶ RecordReplacementEvent ──▶ Replacement state machine
                                                  ├─ audit.replacement_events + trail entry
                                                  ├─ ModelProposed → ReplacementPolicy.CheckModel → queue
                                                  ├─ UnitReserved → notification (unique key)
                                                  ├─ HandedToCustomer → remedy Completed,
                                                  │     catalog.replacement_serials + product_serials
                                                  └─ NotAvailable → RemedyPolicyEvaluator(ReplacementNotAvailable)
                                                        → ClaimRemedy AwaitingDecision → review queue

 Later claim (001 pipeline) ── CaseKnowledgeProvider looks up catalog.replacement_serials
     ├─ claimed serial is a replacement → CaseFacts.ReplacementCover → coverage end, invoice serial
     └─ claimed serial was returned     → RiskAssessor.IntakeSignals PRODUCT_RETURNED_AND_REPLACED
```

### Component changes

| Component | Change |
|-----------|--------|
| `Warranty.Domain` | `Policies/RemedyRules`: `ReplacementRules`, `ReplacementNotAvailable` row (+ validation); `Fulfillment/`: `Replacement` (state machine), `ReplacementLocation`, `FaultyProductReturn`, `ReplacementModelProposal`, `ReplacementUnit`, `ReplacementEvent`, enums; new `RemedyScenario`/pending reason values; `Catalog/RegisteredReplacementSerial`; `ServiceCenter.IssuesReplacements`; `RiskSignalCode.ProductReturnedAndReplaced`; `TrailStep` additions |
| `Warranty.Guardrails` | `Remedies/ReplacementPolicy`; `RemedyPolicyEvaluator` row `ReplacementNotAvailable`; `CaseFacts.ReplacementCover`; `CoverageWindowCalculator` minimum cover end; `EvidenceMatchRules` accepted alternative serial |
| `Warranty.Application` | `Actions/ActionExecutor`: create and cancel replacements (R3, R13); `Fulfillment/RecordReplacementEvent`, `ReplacementLocationQuery`, `ReplacementModelQuery`, read models, claimant projection; `RecordRemedyCompletion` limited to Refund (R12); `ReviewQueueQuery` item kind `ReplacementCheck`; `Adjudication/CaseKnowledgeProvider` replacement lookup (R11); repositories and `IServiceNetwork.ListEligibleAsync(…, purpose)` |
| `Warranty.AI.Harness` | Deterministic code only: `RiskAssessor.IntakeSignals` new signal; `InvoiceValidationTool` accepts the original serial for a replacement unit (R11). No prompt, schema or agent change |
| `Warranty.Infrastructure` | EF configurations and one migration; `xmin` row version on `replacements`; partial unique indexes (one active location, one pending proposal); repositories |
| `Warranty.Integrations.Simulated` | `SimulatedServiceNetwork` honours `issues_replacements`; new notification template |
| `Warranty.MigrationService` | Seed parsing and validation of the `replacement` members; `issuesReplacements`; `AUR-TAB11`; `replacement-scenarios.json` |
| `Warranty.Api` | New endpoints `GET /replacement-locations`, `GET /replacement-models`, `POST /replacement/events` in the `Fulfillment` group with `CrossTenantGuard`; changed claim detail, review queue, remedy decisions, remedy completions, claimant view ([contracts/rest-api.delta.openapi.yaml](./contracts/rest-api.delta.openapi.yaml)) |
| `src/web` | Replacement card and dialogs in the Remedy tab, review queue item, claimant replacement section ([contracts/ui-screens.md](./contracts/ui-screens.md)); types regenerated from the merged contract |

### Error mapping (as 002)

| Situation | HTTP |
|-----------|------|
| Model not allowed, serial rules, missing reason or confirmation, invalid times | 400 ValidationProblem naming the field |
| Reviewer-only event by an agent; self-review | 403 (+ `SELF_REVIEW_REFUSED` for self-review) |
| Other tenant's claim, location or product, unknown ID | 404 + `ACCESS_DENIED` |
| Event not allowed from the current status; lost race on save | 409 with the current status and allowed events |
| Missing or stale `If-Match` | 412 |

### Testing strategy

| Layer | What | How |
|-------|------|-----|
| Unit | `ReplacementPolicy`; evaluator row; `Replacement` transition table and role rules; coverage with minimum cover; rules validation; claimant projection field filtering | xUnit v3, no I/O |
| Architecture | `ReplacementPolicy` in Guardrails references no AI project; only `ActionExecutor` creates or cancels a `Replacement`; existing rules pass | NetArchTest |
| Integration | Quickstart X1–X19 through the API; notification uniqueness; RLS probes and append-only checks; later-claim intake signals and guardrails without an AI run | Testcontainers, WebApplicationFactory, trait `Feature=003` |
| Regression | Full 001 and 002 suites; 002 Replacement completion test moved to `HandedToCustomer` | existing suites |
| Frontend | Buttons from `allowedEvents`, propose dialog, hand-over return checkbox, claimant labels | Vitest + RTL + MSW |

### PoC simplifications and extension points

| PoC simplification | Extension point |
|--------------------|-----------------|
| Staff key in what the location reports | `RecordReplacementEvent`: a location adapter can submit the same events with source `System` |
| Collection only | A `Dispatched`/`Delivered` pair of events and a delivery address on the replacement |
| Stock outside the system | A stock lookup behind `ReplacementModelQuery` and `UnitReserved` |
| Rules seeded in policy front-matter | Policy authoring UI |
| One ready notification, simulated | `INotificationService` real adapter |

## Project Structure

### Documentation (this feature)

```text
specs/003-replacement-fulfilment/
├── spec.md              # Feature specification (/speckit-specify, /speckit-clarify)
├── plan.md              # This file (/speckit-plan)
├── research.md          # Phase 0: decisions R1–R15
├── data-model.md        # Phase 1: tables, state machine, seed changes
├── quickstart.md        # Phase 1: validation scenarios X1–X20
├── contracts/
│   ├── rest-api.delta.openapi.yaml            # merged into 001's canonical contract by the first API task
│   ├── remedy-rules.replacement.schema.json   # merged into 002's remedy-rules.schema.json
│   └── ui-screens.md                          # binding UI contract for src/web tasks
├── checklists/
│   └── requirements.md
└── tasks.md             # Phase 2 (/speckit-tasks)
```

### Source Code (repository root)

Only folders that change are shown; everything else is as in
[002 plan.md](../002-remedy-repair-handover/plan.md).

```text
src/
├── Warranty.Domain/
│   ├── Policies/RemedyRules.cs                 # replacement members
│   ├── Fulfillment/                            # Replacement and its parts, ReplacementEvent, enums
│   ├── Catalog/RegisteredReplacementSerial.cs  # NEW
│   ├── Claims/RiskSignalCode.cs                # PRODUCT_RETURNED_AND_REPLACED
│   └── Common/TrailStep.cs                     # new steps
├── Warranty.Guardrails/
│   ├── Remedies/ReplacementPolicy.cs           # NEW
│   ├── Remedies/RemedyPolicyEvaluator.cs       # ReplacementNotAvailable row
│   ├── Pipeline/GuardrailInput.cs              # CaseFacts.ReplacementCover
│   └── Rules/CoverageWindowCalculator.cs, EvidenceMatchRules.cs
├── Warranty.AI.Harness/
│   ├── Agents/Risk/RiskAssessor.cs             # new intake signal (deterministic)
│   └── Tools/Implementations/InvoiceValidationTool.cs  # accepted original serial
├── Warranty.Application/
│   ├── Actions/ActionExecutor.cs               # create / cancel replacement
│   ├── Fulfillment/                            # RecordReplacementEvent, queries, projection
│   ├── Adjudication/CaseKnowledgeProvider.cs   # replacement serial lookup
│   └── Review/                                 # queue item kind
├── Warranty.Infrastructure/Persistence/        # configurations, repositories, one migration
├── Warranty.Integrations.Simulated/            # network purpose, notification template
├── Warranty.MigrationService/Seeding/          # replacement rules, locations, AUR-TAB11, scenarios
├── Warranty.Api/Endpoints/Fulfillment/         # replacement endpoints
└── web/src/
    ├── features/remedy/                        # ReplacementCard, event dialogs
    ├── features/review/                        # ReplacementCheck item
    ├── features/claimant/                      # replacement section
    └── shared/presentation/                    # replacement labels/tones
seed/tenants/{aurora,borealis}/
├── policies/*.md                               # `remedies.replacement`, `replacementNotAvailable`
├── service-centers.json                        # issuesReplacements
├── products.json, serials.json                 # Aurora: AUR-TAB11
└── replacement-scenarios.json                  # NEW
tests/
├── Warranty.UnitTests/Fulfillment/, Guardrails/Remedies/
└── Warranty.IntegrationTests/Fulfillment/      # trait Feature=003
```

**Structure Decision**: Extend the 002 `Fulfillment` folders; no new project. Replacement rules sit
beside the 002 evaluator in `Warranty.Guardrails/Remedies`, so the architecture tests that keep
Guardrails free of AI cover them. The later-claim rules change only deterministic code that already
produces the related signals and checks.

## Complexity Tracking

No constitution violations; nothing to justify.
