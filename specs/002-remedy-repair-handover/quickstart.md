# Quickstart: Policy-Driven Remedies and Repair-Centre Hand-over

**Feature**: `specs/002-remedy-repair-handover` | **Date**: 2026-10-14 | **Plan**: [plan.md](./plan.md)

Setup, run and test commands are those of
[001 quickstart.md](../001-ai-claim-adjudication/quickstart.md) §1–§3 and §5; nothing new is
installed. Run with `$env:AiGateway__Mode = "replay"` unless a scenario says otherwise; no
scenario here needs a live AI call. Synthetic staff users are those of the Keycloak realm
(`agent.aurora`, `reviewer.aurora`, `auditor.aurora`, and the Borealis equivalents).

## 1. What to check after `aspire run`

1. The migration service applied the 002 migration and re-seeded: `GET /api/policies` as
   `reviewer.aurora` lists `AUR-WP` v1 and v2; their remedy rules appear in the policy versions
   view (staff-only).
2. `seed/tenants/*/remedy-scenarios.json` claims exist as Approved claims with a remedy (§2, R1–R4).
3. Each region of each tenant has at least two active repair centres (FR-045).

## 2. Validation scenarios

Claim data for R1–R12 is in `seed/tenants/*/remedy-scenarios.json` (approved claims created by the
seed, no AI run). R13 runs the full 001 pipeline on the S1 recordings. "Today" is the day you run
it.

| # | Story | Scenario | Expected result |
|---|-------|----------|-----------------|
| R1 | US1 | Aurora covered defect, approved automatically (seeded) | Remedy **Repair**, set by policy, `AUR-WP` row `coveredDefect`; repair **Awaiting hand-over** with the suggested Aurora centre of the claim's region (priority, then name) |
| R2 | US1 | Borealis covered defect, approved automatically (seeded) | Remedy **Repair**, set by policy, `BOR-WP` v1 `coveredDefect` |
| R3 | US1 | `reviewer.aurora` approves an escalated Aurora claim (S5 of 001) and tries to pick Refund via the API | `400` naming the allowed remedies (Repair); with Repair or no remedy → `201`, remedy Repair |
| R4 | US1 | Same facts at Aurora and Borealis (seeded pair), both reach their second failed repair (R8, R9) | Aurora recommends **Replacement**, Borealis **Refund**; each traced to its own policy version and `repairFailed` row |
| R5 | US2 | Take R1: `agent.aurora` records the hand-over (source Phone, centre reference `JOB-1001`) | Repair **At repair centre**; trail `RepairHandedOver` with reported and recorded times; claimant page step "With repair centre" and the centre's address and opening hours, no `JOB-1001` |
| R6 | US2 | On R1 before the hand-over, call **MarkedReady** via the API | `409` naming status Awaiting hand-over and the allowed events |
| R7 | US2 | After R5, mark ready twice: double-click in the UI, then send the same request twice with the old ETag, then two staff at once | One status change to **Ready for collection**; one `integration.notifications` row with template `RepairReadyForCollection` and key `repair-ready:{attemptId}`; the repeats get `412` |
| R8 | US3 | After R5 on an Aurora claim: record **Repair failed** (reason), then **Start new attempt** (product stays at centre), then **Repair failed** again | First failure: failed attempts 1 of 2, new attempt allowed. Second: repair **Repair not possible**, remedy **Awaiting remedy decision** recommending Replacement; claim in the review queue as a Remedy item; claim status still **Approved** |
| R9 | US3 | Same on a Borealis claim | Second failure recommends **Refund**; `reviewer.borealis` confirms with a reason → remedy Refund, a simulated refund request is recorded; history shows the original Repair, both attempts and the reviewer's reason |
| R10 | US3 | Borealis claim at the repair centre (`BOR-SLATE11`, claim value 480): record estimate 288.00, then 288.01 | 288.00 = 60% → repair continues. 288.01 → **Repair not possible** (not economical), failed attempts unchanged, remedy **Awaiting remedy decision** with allowed Refund and Replacement and no recommendation |
| R11 | US4 | Confirm Replacement on R8 and record "Replacement handed to customer"; record Refund completion with reference on R9 | Both remedies **Completed**; claimant page shows "Replacement handed over" / "Refund completed" |
| R12 | US5 | On an Aurora claim at the repair centre after one failure: **Transfer** to the second Aurora centre of the region, then record a hand-over date correction | One active centre (the new one), the old hand-over in the history, failed attempts still 1; the corrected date applies and the original entry stays visible, struck through |
| R13 | US1 | Run 001 S1 end to end (submit through the claimant portal, replay) | Claim **Approved** (system) exactly as in 001; then remedy Repair set by policy, repair Awaiting hand-over with a suggested centre; no `integration.repair_requests` row for this claim |
| R14 | US6 | Borealis accidental damage outside the included incident, with "I have an additional protection plan" ticked and a plan PDF | **Under Review** with reason "protection plan to be checked"; reviewer ticks "Protection plan checked" and approves → remedy from `protectionPlan` (Repair). Without the PDF → **Pending Information** asking for the protection plan document |
| R15 | US6 | Same claim at Aurora (`AUR-TAB10`) with a plan | The plan changes nothing: 001 behaviour (accidental-damage exclusion) applies |
| R16 | FR-022 | As `agent.aurora`, call `GET /api/claims/{borealisClaimId}/repair-centres`, then `POST .../repair/events` with a Borealis `repairCentreId` on an Aurora claim | Both `404`; `ACCESS_DENIED` events; no Borealis data in any response |
| R17 | US2 | As `auditor.aurora`, open the Remedy tab and the trace of R9-like Aurora claim | Everything read-only; trail shows `RemedyRecommended`, `RemedyDecided`, `RepairHandedOver`, `RepairStatusUpdated`, `RepairAttemptEnded` with actor, times and policy version |
| R18 | SC-009 | Run the full 001 scenario suite | Every 001 outcome unchanged (assertions on the simulated repair request now check the remedy and repair instead, research R8) |

SC-001 (staff record a hand-over or status update in under one minute) and SC-006 (auditor
explains the remedy from the trail in under 3 minutes) are checked by a timed walkthrough in the
final validation task, not by automated tests.

Headless example for R6 (token from Keycloak for `agent.aurora`, ETag from `GET /api/claims/{id}`
`repair.etag`):

```powershell
curl -k -X POST -H "Authorization: Bearer $token" -H "If-Match: $etag" -H "Content-Type: application/json" `
  -d '{"type":"MarkedReady","reportedAt":"2026-10-14T09:10:00Z","source":"Phone"}' `
  https://localhost:7443/api/claims/<claim-id>/repair/events
# → 409 application/problem+json naming status AwaitingHandover
```

## 3. Automated tests

Same commands as 001 §5. The new tests are:

| Project | Tests |
|---------|-------|
| `tests/Warranty.UnitTests` | `RemedyPolicyEvaluator` (every row; limit 59/60/61% and exactly at the limit; attempts 1/2/3; missing rules; protection plan allowed or not); `Repair` transition table (every allowed and refused pair); `RemedyRules` validation; claimant projection never exposes staff-only fields; architecture rule that only `ActionExecutor` calls `IRefundService` |
| `tests/Warranty.IntegrationTests` | Remedy scenarios R1–R12 and R14–R17 via the API; R13 on the S1 recordings; notification uniqueness under concurrency (R7); RLS probes on every new table; append-only `audit.repair_events`; migration and seed checks for remedy rules and repair centres |
| `src/web` (Vitest) | Remedy radio group lists only allowed remedies; repair buttons follow `allowedEvents`; 409/412 messages; claimant timeline labels and absence of staff-only fields |

```powershell
dotnet test --project tests/Warranty.UnitTests -- --filter-class "*RemedyPolicyEvaluator*"
dotnet test --project tests/Warranty.IntegrationTests -- --filter-trait "Feature=002"
```

Integration tests of this feature carry the trait `Feature=002` so they can be run alone.
