# Quickstart validation results (T116)

Feature: `specs/001-ai-claim-adjudication`. Scenarios S1–S23 are defined in that feature's
`quickstart.md` §4.

**Status: partly complete.** The automated (headless) validation and a UI pass in replay mode are
done and recorded below. The timed walkthroughs for SC-007 and SC-008 need people who have not
seen the claims, and are still open (section 5). T116 stays unchecked in `tasks.md` until they are
recorded.

## 1. Run

| Item | Value |
|------|-------|
| Date | 2026-10-23 |
| Commit | `1384398` (master after #230) |
| Machine | Windows 11 Pro, Podman 5.8.2 (WSL machine, 8 CPUs, 2 GiB) |
| .NET SDK | 10.0.302 |
| Node | 26.1.0 |
| Aspire | 13.6.0 |
| AI mode | **replay** for everything (no Anthropic key configured; no live model call was made) |

Before the run, `dotnet build Warranty.slnx` failed on a clean master. A MessagePack advisory
published on 2026-10-15 (GHSA-qhrr-8q5h-9q3h) turned NU1902 into an error for `Warranty.AppHost`
and `Warranty.IntegrationTests`, which get MessagePack 2.5.302 from Aspire.Hosting 13.6.0. PR #230
pins the patched 2.5.303. Its CI passed and it is merged, and this run was made on top of it.

## 2. Automated suites

| Suite | Command (quickstart §5) | Result | Time |
|-------|-------------------------|--------|------|
| Build | `dotnet build Warranty.slnx` | 0 warnings, 0 errors | 8 s (incremental) |
| Unit | `dotnet test --project tests/Warranty.UnitTests` | 1797 passed, 0 failed, 0 skipped | 33 s |
| Integration (replay) | `dotnet test --project tests/Warranty.IntegrationTests -- --filter-not-trait "Category=Smoke"` | 193 passed, 0 failed, 0 skipped | 4 min 37 s |
| Aspire smoke | `dotnet test --project tests/Warranty.IntegrationTests -- --filter-trait "Category=Smoke"` | 1 passed: the AppHost boots in replay mode and S1 is approved by the system | 6 min 15 s |
| Frontend lint | `npm --prefix src/web run lint` | clean (`--max-warnings 0`) | — |
| Frontend tests | `npm --prefix src/web test` | 23 files, 179 tests passed | 22 s |
| Frontend build | `npm --prefix src/web run build` | built (only the chunk-size advisory) | — |

No test is skipped, so no `Pending T0xx` test remains on master.

## 3. Scenarios S1–S23

"Headless" names the passing integration tests (`tests/Warranty.IntegrationTests`) that assert the
expected result from quickstart §4. "UI" marks scenarios that were also run by hand through the
SPA (section 4).

| # | Expected result (short) | Headless | UI |
|---|-------------------------|----------|----|
| S1 | Approved by the system, cites `AUR-WP` coverage, trace ends `GuardrailsEvaluated → AutoApproved`, repair request + notification | Pass: `US1_AutomatedAdjudicationTests` (S1, agent channel, trace order, claimant view), smoke test | Pass |
| S2 | Rejected by the system, 12-month clause, `COVERAGE_WINDOW_AGREES` passed | Pass: `US1_…S2_…`, `US6_DecisionTrailTests` (S2) | — |
| S3 | EU purchase approved, only EU clauses | Pass: `US2_…S3_…` | — |
| S4 | Aurora rejects (accidental-damage exclusion), Borealis approves, own clauses only | Pass: `US2_…S4_Aurora…`, `US2_…S4_Borealis…` | — |
| S5 | Under Review, "claim value above auto-approval limit", Aurora queue only | Pass: `US3_…S5_escalates…`, `US3_…S5_claim_detail…` | Pass |
| S6 | Pending Information (invoice), supplement → second round, trail keeps both | Pass: `US5_…S6_round1…`, `US5_…S6_round2…` | — |
| S7 | Under Review with `SERIAL_MISMATCH_PHOTO` / `EVIDENCE_REUSED`, hidden from claimant | Pass: `US4_…S7_suspicious_evidence…` (both), `US4_…S7_reuse…` | — |
| S8 | Under Review, `MANIPULATION_ATTEMPT`, never auto-approved (description, invoice, photo) | Pass: `US4_…S8_…` (S8, S8-invoice, S8-photo) | — |
| S9 | Rejected citing policy v1's battery clause; v2 not retrieved | Pass: `US2_…S9_…` | — |
| S10 | AI unavailable → Under Review, "AI analysis could not be completed" | Pass: `US4_…An_AI_step_that_does_not_complete…` (S10, refusal, truncated) | — |
| S11 | Reviewer rejects S5 against the AI; justification and claimant explanation required; forbidden terms → 400; claimant sees only the explanation | Pass: `US3_…S11_…`, `US6_…The_S11_trace…` | Pass |
| S12 | Borealis ID and unknown ID → identical 404; Aurora `ACCESS_DENIED`; operator-only cross-tenant event | Pass: `CrossTenantAccessTests` (6 endpoints × 2 tests) | — |
| S13 | Borealis oven: Under Review, always-review category + value | Pass: `US3_…S13_escalates…` | — |
| S14 | Auditor trace of S1, S2, S11 with model, prompt version, tokens, cost, tools, clause keys; `hashChainValid: true` | Pass: `US6_DecisionTrailTests` (7 cases incl. tampering → `false`) | Pass (S1, S11) |
| S15 | Wrong email → generic 401 + `CLAIMANT_ACCESS_FAILED`; 6th attempt → 429 | Pass: `ClaimantAccessTests.S15_…` | Pass (401 + event; 429 not tried in UI) |
| S16 | Returned after reviewer request → back to Under Review, `NOT_RETURNED_FROM_REVIEW` failed | Pass: `US5_…S16_…` | — |
| S17 | Illegible invoice twice → round 3 Under Review, `AUTO_INFO_REQUESTS_WITHIN_LIMIT` failed | Pass: `US5_…S17_…` | — |
| S18 | Earlier claim 30 days ago → Under Review `DUPLICATE_SERIAL_CLAIM`; 120 days ago → Approved | Pass: `US4_…S18a_…`, `US4_…S18b_…` | — |
| S19 | Only AI-reported inconsistency → Under Review, risk Medium; no-damage photos → Pending Information | Pass: `US4_…S19_…`, `US5_…Unclear_photos…` | — |
| S20 | Submitter cannot decide own claim (403, `SELF_REVIEW_REFUSED`); another reviewer can | Pass: `US3_…S20_…` (2 tests) | — |
| S21 | Aurora auditor sees the S12/S15/S20 events without details; Borealis sees none; agent → 403 | Pass: `SecurityEventsTests` (11 tests) | Pass (S15 event for Aurora, none for Borealis) |
| S22 | Liquid-damage rejection without liquid damage → Under Review, `GROUNDED_IN_CLAUSE` failed; S4-aurora still rejected | Pass: `US4_…S22_…` | — |
| S23 | Seller/price within tolerance → Approved; price 460 → `SOURCE_INCONSISTENCY` | Pass: `US4_…S23a_…`, `US4_…S23b_…` | — |

All 23 scenarios pass headless.

## 4. UI pass (replay mode)

The stack was started with `AiGateway__Mode=replay` and `dotnet run --project src/Warranty.AppHost`
on Podman, and driven through a browser. The SPA and Keycloak answered about 2 minutes after
start. Claims used the golden scenario data from `seed/golden/scenarios.json` and evidence from
`seed/evidence/`. Replay picks recordings by product serial.

| Step | What was done | Observed |
|------|---------------|----------|
| S1 claimant | Submitted on `aurora.localhost:5173` (two-step form, invoice + 2 photos), then opened the status page | Reference shown at once. Status **Approved** with a plain-language explanation (12-month North America period, manufacturing defect, repair being arranged) and next steps |
| S5 claimant | Submitted the Aurora Book 15 claim (value 1,400) | **Under review**, "A member of our team is reviewing your claim", no risk details |
| S5 reviewer | Signed in as `reviewer.aurora` | Queue shows only this claim, reason "claim value above auto-approval limit", advisory AI recommendation Approve 92 % citing `AUR-WP-2.1 · v2`, risk Low · 0, three evidence files, decision history |
| S11 reviewer | Reject → override dialog. First claimant message contained "fraud" | Refused on the message field: "must not contain risk or fraud terms or internal reference IDs; remove "fraud"". After rewording, the claim is rejected and the queue is empty |
| S11 claimant | Opened the claim with reference + email | **Rejected** with the reviewer's claimant message only; the internal reason is not shown |
| S14 auditor | Signed in as `auditor.aurora`, opened the traces of S1 and S11 | 16 entries each, "Hash chain verified". S1 ends Guardrails 18/18 → Auto-approved → repair request + notification. S11 shows Guardrails 17/18 (`VALUE_ABOVE_LIMIT`) → Escalated → "rejected by reviewer reviewer.aurora, overriding the AI recommendation". Technical view shows model, prompt version, tokens, cache tokens, latency, estimated cost, tool calls with results, resolved clause keys with versions and dates |
| S15 / S21 | Wrong email for the S11 reference, then the Security events page as `auditor.aurora` and `auditor.borealis` | Generic "We couldn't find a claim with those details". Aurora shows one "Claimant access failed" event (actor "claimant channel", target reference, no details or IP). Borealis shows no claims and no events |

## 5. Timed walkthroughs (SC-007, SC-008): open

These need people who have not seen the claims; they were not run. Record times to the nearest
half minute and any screen that slowed the person down.

| Tenant | Walkthrough | Target | Person (role) | Time | What slowed them down |
|--------|-------------|--------|---------------|------|------------------------|
| Aurora | Reviewer opens S5 from the queue and records a decision (SC-008) | < 5 min | | | |
| Borealis | Reviewer opens an escalated claim (S13) from the queue and records a decision (SC-008) | < 5 min | | | |
| Aurora | Auditor explains S1 from the decision trace alone (SC-007) | < 5 min | | | |
| Aurora | Auditor explains S2 from the decision trace alone (SC-007) | < 5 min | | | |
| Aurora | Auditor explains S11 from the decision trace alone (SC-007) | < 5 min | | | |
| Borealis | Auditor explains S4-borealis from the decision trace alone (SC-007) | < 5 min | | | |

S5, S11 and S2 have Aurora data only, so the Borealis rows use S13 and S4-borealis.

## 6. Findings

1. **Trace actor badges mark system steps as HUMAN.** In the decision trace, "Risk evaluated"
   (actor `risk`) and "Auto-approved", "Escalated to human review" and "Action executed" (actor
   `adjudication-service`) carry the **HUMAN** badge. The cause is `trailActorTone` in
   `src/web/src/shared/presentation/trailEntry.ts`. It returns `system` only for the literal actor
   `system` and `ai` only for agent names, so every other actor becomes `human`. This contradicts the
   AI/human/system visual language in `ui-design.md`. It could mislead an auditor in the SC-007
   walkthrough, because an automatic approval reads as a human action. Recommended fix: treat
   `adjudication-service` and the deterministic `risk` step as system, and keep `human` for staff
   subjects and the claimant.
2. **The reviewer step shows a raw subject ID.** "Reviewer decision" and the action after it show the
   reviewer's Keycloak subject (a GUID) as the actor badge name. The entry text does name
   `reviewer.aurora`, so the information is there, but the badge is unreadable.
3. **Redaction placeholder in the operational trace text.** "Customer verified" reads "Customer record
   found (country US, region [CUSTOMER])". The placeholder looks like a defect to an auditor;
   omitting the region or naming it would read better.
4. **The claimant re-enters the email after submitting.** "Check status" on the confirmation screen
   opens the access form with the reference pre-filled but asks for the email or phone again. This
   is consistent with FR-037a (no session from submission), so it is noted only as friction.

## 7. Not covered here

- **Live models.** Every run used recordings; no live model call was made. The live S1 timing and
  cache-read measurements belong to T114 (`artifacts/validation/performance.md`), which needs an
  Anthropic key and spends money.
- **S15 rate limit in the UI.** The 429 on the sixth attempt was verified headless only.
