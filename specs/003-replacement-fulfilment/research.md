# Research: Replacement Fulfilment and Faulty-Product Return

**Feature**: `specs/003-replacement-fulfilment` | **Date**: 2026-10-23 | **Plan**: [plan.md](./plan.md)

Decisions are numbered R1–R15 in this feature. References of the form "002 R5" or "001 R25" point
to the research of those features. Every decision was checked against the code at `4261a23`
(`origin/master` `ad2881e` plus the 002 documents) and against the 002 design, which is specified
but not implemented: wherever this feature extends a 002 type, table or endpoint, the extension is
applied on top of the 002 design.

## R1 — Replacement rules extend the 002 `remedies` block

- **Decision**: The policy front-matter `remedies` block (002 R1, stored in
  `policy.policy_versions.remedy_rules`) gains an optional `replacement` object
  (`productRule`, `returnRule`, `minimumCoverMonths`) and an optional row
  `replacementNotAvailable` with the same shape as the other remedy rows (`remedy` or `allowed`).
  Validated by [contracts/remedy-rules.replacement.schema.json](./contracts/remedy-rules.replacement.schema.json),
  which the first 003 domain task merges into 002's `remedy-rules.schema.json`. When `replacement`
  is absent the safest defaults apply: `SameModel`, `AtCollection`, `minimumCoverMonths: 0`
  (spec edge case "policy version without replacement rules").
- **Rationale**: FR-001 and FR-005 require the rules to be versioned with the policy, exactly like
  the 002 remedy table. One block keeps one validation path and one column.
- **Alternatives considered**: A second `replacement_rules` column (rejected: two blocks that must be
  validated together); rules per product category (rejected: not asked for, and the seed has one
  rule per company).

## R2 — Deterministic replacement rules in `Warranty.Guardrails/Remedies`

- **Decision**: Two pure additions next to 002's `RemedyPolicyEvaluator`:
  - `RemedyPolicyEvaluator` gains the scenario `ReplacementNotAvailable`, read from the
    `replacementNotAvailable` row; a missing row gives `NO_REMEDY_RULE` (manual review, 002 FR-012).
    Pending reason `REPLACEMENT_NOT_AVAILABLE` (a single remedy still needs a reviewer, FR-026, as
    002's `REPAIR_FAILED_LIMIT`).
  - `ReplacementPolicy` with `CheckModel(rules, originalProduct, proposedProduct)` →
    `SameModel` | `NeedsReviewerApproval` | `Refused(reason)` (FR-002, FR-023), and
    `InitialStatus(rules, faultyProductHeldByCompany)` → `AwaitingReturn` | `AwaitingUnit` (FR-013).
- **Rationale**: Same home and the same architecture tests as the 002 evaluator (no AI reference).
  Every company runs the same code (FR-006).
- **Alternatives considered**: Checking models inside the `Replacement` aggregate (rejected: mixes
  policy rules with the state machine, as 002 R2).

## R3 — A `Replacement` aggregate, created by the `ActionExecutor`

- **Decision**: New aggregate `Warranty.Domain/Fulfillment/Replacement` owns the status, the active
  location assignment, the return, the pending model proposal and the current unit, and enforces
  the transition table of [data-model.md](./data-model.md). The `ActionExecutor` creates it in the
  same transaction in which the remedy becomes Replacement — at approval (002 R3) or by a reviewer's
  remedy decision (002 R4) — with the suggested location (R8) and the initial status (R2, R9).
- **Rationale**: Mirrors 002's `Repair` creation, so a claim never has a Replacement remedy without
  a replacement record.
- **Alternatives considered**: Creating it lazily on the first staff action (rejected: no suggested
  location and no claimant instructions until someone opens the claim).

## R4 — Staff updates through one command with optimistic concurrency

- **Decision**: `Warranty.Application/Fulfillment/RecordReplacementEvent` (one command, one event
  type per action), `If-Match` on the replacement's row version, `412` for a stale ETag and `409`
  for a lost race or an event not allowed from the current status — exactly as 002 R5. Human-reported
  facts need no `ApprovedAction`.
- **Rationale**: Same rationale as 002 R5; the UI and tests reuse the repair patterns.

## R5 — Model approval and return-mismatch resolution are reviewer events

- **Decision**: `ModelApproved`, `ModelDeclined` and `ReturnAccepted` are event types of the same
  command, allowed only for the `claims-reviewer` role. The command refuses a reviewer who
  submitted the claim (`403 SELF_REVIEW_REFUSED` and a security event, as 002 R4). A pending
  proposal or an unresolved return mismatch puts the claim in the review queue as a new item kind
  `ReplacementCheck` with reason `MODEL_APPROVAL` or `RETURN_MISMATCH`.
- **Rationale**: Choosing a different model is a decision about what the customer receives
  (constitution IV), but it moves no money and has no external effect, so it does not need to go
  through the `ActionExecutor`. Keeping it in the replacement command keeps the history in one
  event stream. `ReturnAccepted` lets a reviewer accept a returned product after checking a
  mismatch (for example a typo in the claim's serial); otherwise staff correct the entry or a
  reviewer changes the remedy.
- **Alternatives considered**: Routing model approval through the `ActionExecutor` (rejected: no
  consequential effect to guard); letting staff choose an equivalent without a reviewer (rejected
  in clarification).

## R6 — History: append-only replacement events plus the decision trail

- **Decision**: Every update writes one row to `audit.replacement_events` (append-only by the
  existing `audit` grants and trigger) and one decision trail entry. New trail steps:
  `ReplacementLocationAssigned`, `FaultyProductReturned`, `ReplacementModelProposed`,
  `ReplacementModelDecided`, `ReplacementStatusUpdated`, `ReplacementUnitRegistered`. Completion
  reuses 002's `RemedyCompleted`. Corrections append a `Correction` event that references the
  corrected one (FR-017).
- **Rationale**: Same split as 002 R6: readable hash-chained trail, queryable structured events.

## R7 — Exactly one ready notification per reserved unit

- **Decision**: Reuse 002's `integration.notifications.dedupe_key` (002 R7) with the key
  `replacement-ready:{unitId}` and the new template `ReplacementReadyForCollection`, queued in the
  same transaction as `UnitReserved`. A refused unit or a move ends the unit; a newly reserved unit
  has a new ID and gets its own notification (FR-031, User Story 5 scenario 3).
- **Rationale**: Database-enforced uniqueness, as for repairs (SC-005).

## R8 — Replacement locations are flagged service centres

- **Decision**: `integration.service_centers` (already extended by 002 R8) gains
  `issues_replacements bool default false`. `IServiceNetwork.ListEligibleAsync(region, category)`
  from 002 gains a `purpose` argument (`Repair` | `Replacement`); for `Replacement` it also requires
  `issues_replacements`. The suggestion rule is unchanged: active, region, category, priority, name.
- **Rationale**: FR-007 forbids a second list; one flag is the smallest change.

## R9 — "Faulty product already with the company"

- **Decision**: When the remedy changes from Repair to Replacement, the executor checks the claim's
  002 `Repair`: if its status is `AtRepairCentre` or `RepairNotPossible`, its active hand-over has a
  `handed_over_on` date, and the last `NewAttemptStarted` did not record `ReturnedToCustomer`, the
  faulty product is held by the company. The replacement then starts with a `ReturnRecorded` event
  of source `System` that references the repair hand-over (FR-020), and the return rule is satisfied
  for both `AtCollection` and `BeforeReservation`.
- **Rationale**: Uses only facts 002 already records; no new staff step.
- **Alternatives considered**: Asking staff to confirm every time (rejected: FR-020 says the customer
  must not be asked, and staff would only re-key the repair hand-over).

## R10 — Registering the replacement unit and the returned unit

- **Decision**: New table `catalog.replacement_serials`, written in the same transaction as
  `HandedToCustomer` (FR-025): the handed-out serial and its product, the original serial, the claim,
  the original purchase date, the hand-over date and the computed `cover_end_date`. When the handed-out
  serial is not yet in `catalog.product_serials`, it is inserted for the replacement product (a
  catalog serial is accepted at `UnitReserved` only when it belongs to the reserved model), so the
  existing catalog lookup (`IErpSerialRegistry`, `CaseKnowledgeProvider`) finds it. Both writes are
  the app role's normal DML on `catalog` (grants in `001_roles_and_rls.sql`), under RLS.
- **Rationale**: One table answers both later-claim questions: "is this serial a replacement?"
  (lookup by `serial_number`) and "was this serial returned and replaced?" (lookup by
  `original_serial`). Inserting into `product_serials` avoids touching the existing
  `PRODUCT_NOT_IN_CATALOG` logic for the replacement serial (FR-029).
- **Alternatives considered**: A status column on `product_serials` (rejected: the link to the claim,
  purchase date and cover end would still need a home).

## R11 — Later claims on replacement units (FR-029, FR-030)

- **Decision**:
  - `CaseKnowledgeProvider` looks the claimed serial up in `catalog.replacement_serials` and adds a
    `ReplacementCover(originalSerial, originalPurchaseDate, coverEndDate)` to the guardrail
    `CaseFacts`.
  - `CoverageWindowCalculator.Calculate` gains an optional `minimumCoverEnd`; the coverage end is
    the later of the window computed from the **original** purchase date and `minimumCoverEnd`.
  - The deterministic invoice check (`InvoiceValidationTool` / `EvidenceMatchRules`) accepts the
    original serial on the invoice for a replacement unit, because the customer's invoice shows the
    original product; the comparison rule itself (001 FR-016 normalisation) is unchanged.
  - `RiskAssessor.IntakeSignals` adds a deterministic signal `PRODUCT_RETURNED_AND_REPLACED`
    (severity Medium) when the claimed serial is an `original_serial` in `catalog.replacement_serials`. Any
    risk signal prevents automatic approval (001 FR-027), so the claim goes to human review
    (FR-030); an integration test asserts it.
  - The accidental-damage incident count (`PriorApprovedAccidental`) includes approved claims of the
    original serial, so a replacement does not reset Borealis's one-incident allowance.
- **Rationale**: All changes are deterministic and outside prompts and model output schemas, so the
  replay recordings stay valid. The new signal lives in the existing deterministic part of the
  harness (`RiskAssessor.IntakeSignals`), which already produces `PRODUCT_NOT_IN_CATALOG` and
  `DUPLICATE_SERIAL_CLAIM`.
- **Open item for the implementing task**: confirm that the Evidence agent's prompt shows the
  claimed serial only as data; if it also asks the model to compare serials, pass the original
  serial as an accepted alternative there too, and re-record nothing (no golden case uses a
  replacement serial).
- **Alternatives considered**: Re-using the original serial for the replacement in the catalog
  (rejected: a serial identifies one physical unit, glossary "Serial number").

## R12 — Remedy completion moves into the replacement

- **Decision**: For Replacement, the remedy becomes `Completed` through the `HandedToCustomer` event
  (as a repair completes through `Collected`). `POST /remedy-completions` (002) accepts only `Refund`
  from now on; `claims.remedy_completions.remedy` keeps `Replacement` only for rows written before
  this feature (spec edge case). This replaces 002 FR-034.
- **Rationale**: One way to complete a replacement, with the serial registration and return checks
  attached.

## R13 — Cancellation on a remedy change

- **Decision**: 002's `ExecuteRemedyDecisionAsync` allows a change away from Replacement until
  `HandedToCustomer`. In the same transaction it records a `Cancelled` event (source `System`) and
  sets the replacement to `Cancelled` (FR-028). The return stays recorded.
- **Rationale**: Keeps remedy and replacement consistent without a second staff step.

## R14 — Seed data

- **Decision**: Add `replacement` blocks and `replacementNotAvailable: { remedy: Refund }` to the
  existing `AUR-WP` v1, v2 and `BOR-WP` v1 front-matter (FR-037; clause text unchanged, 002 R12).
  Mark every active service centre `issuesReplacements: true` (FR-038 is then met by the 002
  centres). Add a second Aurora tablet model `AUR-TAB11` (claim value 520) with serials, so an
  equivalent can be proposed (FR-038); no existing product changes. New
  `seed/tenants/*/replacement-scenarios.json` with approved claims whose remedy is Replacement, each
  return rule, "not available", an Aurora equivalent model, and one "same facts, different company"
  pair (FR-039).
- **Rationale**: Adding a product leaves 001 golden cases and recordings untouched (they reference
  existing models and serials only).

## R15 — Test strategy

- **Decision**: Unit tests for `ReplacementPolicy` (same model; equal, higher and lower claim value;
  other category; missing claim value; Borealis same-model-only), the `ReplacementNotAvailable`
  evaluator row, the `Replacement` transition table (every allowed and refused pair), the coverage
  calculation with a minimum cover (end of original window before, on and after the minimum), and
  the claimant projection. Integration tests (trait `Feature=003`) create approved claims through
  the domain as in 002 R14 and cover the API scenarios, notification uniqueness, RLS probes on the
  new tables, append-only events, and the later-claim rules through the intake signals and
  guardrails without an AI run. No new AI recordings.
