# Feature Specification: Policy-Driven Remedies and Repair-Centre Hand-over

**Feature Branch**: `docs/002-repair-handover-spec` — spec directory `specs/002-remedy-repair-handover`

**Created**: 2026-10-14

**Status**: Draft

**Input**: User description: "Policy-driven remedies and repair-centre hand-over coordination for
approved warranty claims. Each company (tenant) configures a versioned remedy table in its policy
(covered defect, repair not economical, repair failed N times, damage covered by protection plan);
the platform applies the same decision process to all companies; remedy types are Repair,
Replacement, Refund (refund already exists); reviewer confirms unless the policy gives exactly one
remedy; AI never chooses a remedy. For Repair: system suggests a same-tenant repair centre with
staff override, staff record hand-over, readiness (At repair centre, Ready for collection,
Collected, Repair not possible) as reported by the repair centre by phone/email; repair centres
keep their own software, no integration; failed repairs are counted and trigger policy
re-evaluation; exactly one customer notification on Ready for collection; claimant status page
timeline; append-only audit; tenant isolation. Seed Aurora with Company A rules and Borealis with
Company B rules. Out of scope: workshop operations, money movement, replacement logistics,
repair-centre software integration, policy editor."

**Builds on**: `specs/001-ai-claim-adjudication`. References of the form "001 FR-036" point to
that specification.

## Clarifications

### Session 2026-10-13 (requirements and team review)

- Q: Who decides the remedy of an approved claim? → A: The company's policy recommends it. It is
  set automatically only when the policy gives exactly one remedy; otherwise a reviewer chooses
  from the remedies the policy allows. The AI never chooses or changes a remedy.
- Q: Does the remedy decision change the approval? → A: No. The claim decision, the remedy and
  the repair status are separate. The claim stays Approved whatever happens to the remedy.
- Q: How does WARRANTY-AI learn what happens at the repair centre? → A: Repair centres keep their
  own software, which is not connected. The repair centre tells staff by phone or email, and staff
  record it on the claim.
- Q: Does tracking end at "Ready for collection"? → A: No. "Collected" is the end of a successful
  repair.
- Q: Who picks the repair centre? → A: The system suggests one; staff may pick another eligible
  centre of the same company. The customer does not choose.
- Q: What happens when a repair fails? → A: The policy is evaluated again with the number of
  failed attempts. A failed repair never triggers a refund or any other remedy on its own; a
  reviewer confirms every remedy change.
- Q: Is replacement handled in the system? → A: It is decided and recorded here. Stock, ordering
  and shipping happen outside the system.

### Session 2026-10-14 (proposed defaults accepted from the team review page)

- Q: What counts as a failed repair? → A: Every repair attempt the repair centre reports as
  unsuccessful counts as one failed attempt. Transfers between centres never count.
- Q: When is a repair not economical? → A: When the repair estimate reported by the repair centre
  is above a limit set in the policy as a percentage of the product's claim value (001 FR-001).
  Company B (Borealis) uses 60% in the PoC.
- Q: How does a customer prove a protection plan? → A: By stating it on the claim and attaching
  the plan document as evidence. A reviewer checks it; a claim that relies on a protection plan is
  never decided automatically.
- Q: How is a replacement tracked? → A: Staff record "Replacement handed to customer". There is no
  collection timeline like a repair.
- Q: How is a refund carried out? → A: Through the application's existing refund option. This
  feature records the refund remedy and its completion and moves no money itself.
- Q: Who may confirm or change a remedy? → A: Reviewers only. Claims agents and reviewers may
  record hand-over and repair status updates. Auditors are read-only.

## User Scenarios & Testing *(mandatory)*

### Actors

- **Claimant** — the end customer. Hands the product to the repair centre, collects it, and
  follows the claim on the claim status page.
- **Claims Agent** — a tenant staff member who records the hand-over and the repair centre's
  updates on the claim.
- **Claims Reviewer** — a tenant staff member who also confirms or changes remedies.
- **Auditor** — a tenant staff member who inspects the remedy and repair history (read-only).
- **Repair Centre** — an external repair location of a tenant, with its own workshop software. It
  only reports facts: product received, repair estimate, ready, repair failed, collected.
- **Tenant (company)** — configures its own remedy rules in its warranty policy.

Every staff user belongs to exactly one tenant (001 FR-005). A reviewer may never confirm or change
the remedy of a claim they submitted (001 FR-034).

### The shared decision process

Every company's claims go through the same steps; only the rules come from the company's policy:

1. Is the product inside warranty? If not, reject (existing, 001).
2. Is the damage excluded? If so, reject, unless the policy lets a protection plan cover it
   (existing, extended by this feature).
3. Which remedy applies? Taken from the company's remedy table (new).
4. Confirm the remedy: automatically when the policy gives exactly one, otherwise by a reviewer
   (new).

Any step the policy does not answer clearly goes to manual review. After a failed repair, or when
a repair is not economical, step 3 is evaluated again.

Example used for the PoC seed (two companies that both sell washing machines):

| Scenario | Company A (seeded as Aurora) | Company B (seeded as Borealis) |
|---|---|---|
| Manufacturing defect within warranty | Repair first | Repair; Refund or Replacement if repair is not economical |
| Repair unsuccessful twice | Replacement | Refund |
| Product outside warranty | Reject warranty claim | Reject warranty claim |
| Physical damage caused by customer | Excluded | Excluded, unless an additional protection plan covers it |
| Policy is unclear | Manual review | Manual review |

### User Story 1 - Remedy decided by the company's policy (Priority: P1)

When a claim is approved, automatically or by a reviewer, the system determines the claim's
scenario and looks up the remedy in the company's applicable policy version. When the policy gives
exactly one remedy, it is set automatically. When the policy allows a choice, for example "Refund
or Replacement", a reviewer chooses from the allowed remedies. A reviewer approving a claim sees
the recommended remedy pre-selected and can only pick remedies the policy allows. The same claim
facts at two companies lead to the remedy each company's policy dictates.

**Why this priority**: The remedy is what the customer actually receives. Without it, an approval
has no follow-up, and every later step (hand-over, refund, replacement) depends on it. It is a
viable MVP on its own: approved claims carry a policy-grounded, explainable remedy.

**Independent Test**: Approve a covered-defect claim at Aurora and at Borealis; verify both get
Repair with the policy version and table row recorded. Configure a scenario where Borealis allows
Refund or Replacement; verify the system does not pick and a reviewer must choose. Try to select
Refund for an Aurora covered defect; verify it is refused.

**Acceptance Scenarios**:

1. **Given** an Aurora claim for a covered manufacturing defect, **When** it is approved
   automatically, **Then** its remedy is set to Repair automatically, and the decision trail names
   the policy version and the remedy table row used.
2. **Given** a claim whose policy row allows more than one remedy without an order, **When** the
   claim is approved automatically, **Then** the claim stays Approved, its remedy shows "Awaiting
   remedy decision", and it appears in the reviewers' queue for a remedy choice.
3. **Given** a reviewer approving an escalated claim, **When** they open the approval, **Then** the
   recommended remedy is pre-selected and only remedies the policy allows for the claim's scenario
   can be chosen.
4. **Given** a reviewer, **When** they try to choose a remedy the policy does not list for the
   scenario, **Then** the choice is refused with a message naming the allowed remedies.
5. **Given** a claim whose applicable policy has no remedy row for its scenario, **When** it is
   approved, **Then** the remedy goes to manual review with the reason "policy gives no remedy for
   this scenario".
6. **Given** identical claim facts at Aurora and Borealis, **When** both reach the same scenario,
   **Then** each gets the remedy its own policy gives, each traced to its own policy version.

---

### User Story 2 - Repair hand-over, readiness and customer notification (Priority: P1)

For a claim whose remedy is Repair, the system suggests a repair centre of the same company for
the claim's region and product category. Staff confirm it or pick another eligible centre. The
customer sees on the claim status page where to take the product. When the repair centre tells
staff it has received the product, staff record the hand-over with the centre's own job reference.
When the centre reports the product is ready, staff mark it ready; the customer is notified once
and the status page shows where to collect it. When the customer has collected it, staff record
the collection.

**Why this priority**: This is the coordination the business is missing today: knowing where the
product is and telling the customer when it is ready. Together with User Story 1 it delivers the
full happy path.

**Independent Test**: Take an approved Aurora claim with remedy Repair through suggested centre,
hand-over, ready and collected; verify each step on the claim page and the decision trail, exactly
one customer notification, and the claimant status page at each step.

**Acceptance Scenarios**:

1. **Given** a claim with remedy Repair, **When** the remedy is set, **Then** the repair status is
   "Awaiting hand-over" and an eligible repair centre is suggested.
2. **Given** a suggested centre, **When** staff pick another active centre of the same company that
   serves the region and product category, **Then** it becomes the claim's repair centre and the
   change is recorded with the staff member and a reason.
3. **Given** "Awaiting hand-over", **When** staff record the hand-over with date, centre reference
   and note, **Then** the status becomes "At repair centre" and both the reported time and the
   recording time are stored.
4. **Given** "Awaiting hand-over", **When** staff try to mark the repair ready, **Then** it is
   refused with the message that the hand-over must be recorded first.
5. **Given** "At repair centre", **When** staff mark it ready, **Then** the status becomes "Ready
   for collection", one customer notification is queued with the centre's name, address and
   opening hours, and the claimant status page shows the same.
6. **Given** "Ready for collection", **When** staff mark it ready again or two staff members mark
   it at the same time, **Then** there is one status change and one notification.
7. **Given** "Ready for collection", **When** staff record the collection, **Then** the status
   becomes "Collected" and the repair is complete.
8. **Given** "Collected", **When** staff try any further status change, **Then** it is refused.
9. **Given** the claimant opens the status page, **When** the repair is in any state, **Then** they
   see the remedy and the timeline (handed over, with repair centre, ready for collection,
   collected) and no staff notes, staff names or risk information.

---

### User Story 3 - Failed or uneconomical repair and policy re-evaluation (Priority: P2)

The repair centre may report that the repair failed, or give a repair estimate above the
company's economic limit. Staff record it. The system counts failed attempts and evaluates the
company's policy again. Below the attempt limit, a new repair attempt starts. At the limit, or when
the repair is not economical, the policy's next remedy is recommended and a reviewer confirms it.
The original remedy and every attempt remain in the history; the claim stays Approved.

**Why this priority**: Without this, a failed repair is a dead end. It is the second half of the
company-specific rules (Company A: Replacement after two failures; Company B: Refund) but it is
only needed once repairs are being tracked (User Story 2).

**Independent Test**: Record two failed repairs on an Aurora claim and on a Borealis claim; verify
the first failure starts a new attempt, the second leads to a Replacement (Aurora) or Refund
(Borealis) recommendation that a reviewer must confirm. Record a Borealis estimate above 60% of
the claim value; verify the reviewer must choose between Refund and Replacement.

**Acceptance Scenarios**:

1. **Given** "At repair centre", **When** staff record that the repair failed with a reason,
   **Then** the status becomes "Repair not possible", the failed-attempt count increases by one,
   and the policy is evaluated again.
2. **Given** a first failed attempt and an attempt limit of 2, **When** the policy is evaluated,
   **Then** a new repair attempt starts and the customer sees that the repair continues.
3. **Given** an Aurora claim with a second failed attempt, **When** the policy is evaluated,
   **Then** Replacement is recommended and the remedy shows "Awaiting remedy decision" until a
   reviewer confirms it.
4. **Given** a Borealis claim with a second failed attempt, **When** the policy is evaluated,
   **Then** Refund is recommended and a reviewer must confirm it.
5. **Given** a Borealis claim at the repair centre, **When** staff record a repair estimate above
   60% of the product's claim value, **Then** the repair stops as "not economical", it does not
   count as a failed attempt, and the reviewer must choose between Refund and Replacement.
6. **Given** a policy that uses the economic rule, **When** no estimate has been recorded, **Then**
   the repair continues; a missing estimate never ends a repair.
7. **Given** a confirmed remedy change to Refund or Replacement, **When** the history is opened,
   **Then** it shows the original Repair remedy, every attempt and its result, the policy version
   used and the reviewer's reason.
8. **Given** the claimant opens the status page after a failed repair, **When** no new remedy is
   confirmed yet, **Then** they see "Repair not possible. Your claim is being reviewed." without
   the internal reason.

---

### User Story 4 - Replacement and refund completion (Priority: P2)

When the remedy is Replacement, staff record when the replacement was handed to the customer.
When the remedy is Refund, the refund is carried out through the application's existing refund
option, and the claim shows when it was completed. The customer sees the remedy and whether it is
completed.

**Why this priority**: Both remedies can result from User Story 1 or 3, and the claim needs a
clear end state for them. The physical and financial work happens outside this feature, so the
scope is small.

**Independent Test**: Confirm Replacement on one claim and Refund on another; record completion;
verify the claim and the claimant status page show each remedy as completed and the trail records
who completed it and when.

**Acceptance Scenarios**:

1. **Given** a claim with remedy Replacement, **When** staff record "Replacement handed to
   customer" with the date, **Then** the remedy is completed.
2. **Given** a claim with remedy Refund, **When** the refund is completed through the existing
   refund option, **Then** the claim shows the refund as completed with the date and reference.
3. **Given** a completed Replacement or Refund, **When** the claimant opens the status page,
   **Then** they see the remedy and that it is completed.

---

### User Story 5 - Repair-centre transfer and corrections (Priority: P3)

A product sometimes moves to another repair centre, or staff record something by mistake. Staff
can transfer the repair to another eligible centre of the same company and correct a mistaken
entry with a reason. Nothing is overwritten: the old centre and the original entry stay in the
history.

**Why this priority**: Needed for real-world operation but not for the core demonstration.

**Independent Test**: Transfer an "At repair centre" claim to another centre; verify one active
centre, the old one in the history, an unchanged attempt count and the new centre on the status
page. Correct a mistaken hand-over date; verify the original and the correction both appear.

**Acceptance Scenarios**:

1. **Given** "Awaiting hand-over" or "At repair centre", **When** staff transfer the repair to
   another eligible centre with a reason, **Then** the new centre becomes the only active centre,
   the old one stays in the history, and the failed-attempt count does not change.
2. **Given** a transfer of a product already at a centre, **When** it is recorded, **Then** the
   status becomes "Awaiting hand-over" at the new centre until staff record the new hand-over.
3. **Given** a mistaken entry, **When** staff record a correction with a reason, **Then** the
   corrected value applies, and both the original entry and the correction remain visible in the
   history.
4. **Given** a centre of another company, **When** staff try to transfer to it, **Then** it is
   refused and recorded as a security event.

---

### User Story 6 - Protection plan cover (Priority: P3)

For a company whose policy allows it, damage caused by the customer is covered when the customer
has an additional protection plan. The claimant states the plan on the claim and attaches the plan
document. A reviewer checks it. When the plan is confirmed, the claim is covered and the policy's
protection-plan remedy row applies.

**Why this priority**: It shows a real policy difference between the two companies, but it needs a
small change to claim intake and always involves a reviewer.

**Independent Test**: Submit a Borealis claim for customer-caused damage with a plan document;
verify it goes to human review, and after the reviewer confirms the plan, the claim is approved
with the protection-plan remedy. Submit the same claim without a plan; verify it is excluded as
today. Submit the same claim with a plan to Aurora; verify the plan does not lift the exclusion.

**Acceptance Scenarios**:

1. **Given** a Borealis claim for customer-caused damage that states a protection plan with a plan
   document, **When** it is evaluated, **Then** it is never decided automatically and goes to human
   review with the reason "protection plan to be checked".
2. **Given** a reviewer who confirms the plan, **When** they approve the claim, **Then** the remedy
   comes from Borealis's protection-plan row.
3. **Given** a claim that states a plan without a plan document, **When** it is evaluated,
   **Then** the plan document is requested as missing information.
4. **Given** an Aurora claim for customer-caused damage with a plan, **When** it is evaluated,
   **Then** the exclusion applies, because Aurora's policy does not let a plan lift it.

---

### Edge Cases

- **No eligible repair centre** for the claim's region and product category: no centre is
  suggested; staff may pick any active centre of the same company with a reason, or a reviewer may
  change the remedy to another remedy the policy allows.
- **Policy version without remedy rules** (for example a version that predates this feature): the
  remedy goes to manual review with the reason "policy gives no remedy for this scenario".
- **Claims approved before this feature went live**: they keep their existing outcome and records;
  no remedy is assigned to them retroactively.
- **Repair estimate exactly at the economic limit**: treated as economical; only estimates strictly
  above the limit stop the repair.
- **Repair estimate recorded for a policy without an economic rule**: stored for information; it
  never ends the repair.
- **Failure after a transfer**: counts as one failed attempt at the new centre; the transfer itself
  never counts.
- **Fault returns after the product was collected**: handled as a new claim under 001 (including
  its duplicate-claim rule); attempts are counted within one claim only.
- **Two staff members update the same claim at the same time**: one update applies; the other is
  refused with a message that the claim changed and must be reloaded.
- **Staff mark ready or collected with a reported time in the future, or before the hand-over**:
  refused with a clear message.
- **Remedy change requested to a remedy the policy does not allow**: refused, even for a reviewer.
- **Reviewer tries to confirm the remedy of a claim they submitted**: refused and recorded as a
  security event (as 001 FR-034).
- **Claimant opens the status page while the remedy awaits a reviewer**: sees "We are deciding how
  to resolve your claim" and no internal details.
- **Correction of "Ready for collection" back to "At repair centre"**: allowed with a reason; the
  notification already queued is not withdrawn, and no second notification is queued for the same
  attempt.
- **Cross-tenant references** (repair centre, claim or policy of another company) in any request:
  refused without revealing whether the target exists, and recorded as a security event.

## Requirements *(mandatory)*

### Functional Requirements

#### Company remedy rules

- **FR-001**: Remedies MUST come from the claim's company policy, never from a platform-wide rule.
  Each policy version MUST be able to hold a remedy table with these rows: covered defect; repair
  not economical; repair failed N times; damage covered by a protection plan.
- **FR-002**: The platform MUST support three remedy types: Repair, Replacement and Refund. A
  remedy the policy does not list for a scenario MUST NOT be recommended or selectable for that
  scenario, by the system or by a reviewer.
- **FR-003**: Each row MUST give either exactly one remedy or a list of allowed remedies. A list
  without an order MUST be decided by a reviewer.
- **FR-004**: A policy MAY define an economic limit for repairs as a percentage of the product's
  claim value. A repair estimate strictly above the limit makes the repair not economical.
- **FR-005**: A policy MAY define the number of failed repair attempts after which its "repair
  failed" row applies.
- **FR-006**: A policy MAY allow an additional protection plan to lift its exclusion for damage
  caused by the customer. Without that permission, a protection plan MUST NOT change the coverage
  decision.
- **FR-007**: Remedy rules MUST be part of the policy version and MUST be selected with it using
  the existing version-selection rule (001 clarification: version in effect on the purchase date).
  Once a version is in use, its remedy rules MUST NOT change; a change creates a new version.
- **FR-008**: All companies MUST go through the same decision process (see "The shared decision
  process"); company differences MUST come only from policy data.

#### Remedy decision

- **FR-009**: Every claim approved after this feature is live MUST get exactly one remedy, taken
  from the remedies its applicable policy version allows for the claim's scenario. The initial
  scenario is "covered defect", or "damage covered by a protection plan" when a confirmed plan
  lifted an exclusion.
- **FR-010**: The remedy recommendation MUST be deterministic: the same facts and the same policy
  version always give the same result. It MUST record the policy version and the table row that
  produced it.
- **FR-011**: When the row gives exactly one remedy, the system MUST set it automatically, for
  automatic and reviewer approvals alike. Otherwise the remedy MUST show "Awaiting remedy decision"
  and the claim MUST appear in the reviewers' queue for a remedy choice; the claim's Approved
  outcome MUST NOT be held back.
- **FR-012**: When the policy gives no remedy for the scenario, or information needed to choose one
  is missing, the remedy MUST go to manual review with the reason recorded. Missing information
  MUST NOT lead to a rejection or to an automatic remedy.
- **FR-013**: A reviewer approving an escalated claim MUST choose the remedy as part of the
  approval, from the remedies the policy allows, with the recommendation pre-selected.
- **FR-014**: AI components MUST NOT choose, recommend or change a remedy. The remedy is derived
  only from the deterministic policy evaluation and from reviewer decisions.
- **FR-015**: Only reviewers MAY confirm or change a remedy. Every change after the first remedy
  MUST name a remedy the policy allows and include a written reason. A reviewer MUST NOT confirm or
  change the remedy of a claim they submitted.
- **FR-016**: The remedy and its changes MUST NOT change the claim's final outcome (001 FR-036).

#### Repair centres and hand-over

- **FR-017**: Each company MUST have its own repair centres with name, region, product categories
  served, address, opening hours, contact details, an active flag and a priority. These are the
  same locations as the service centres of 001, not a second list.
- **FR-018**: For a Repair remedy, the system MUST suggest the active repair centre of the claim's
  company that serves the claim's region and product category, choosing the highest priority and
  then the name. A suggestion MUST NOT count as a hand-over.
- **FR-019**: Staff MUST be able to pick another active centre of the same company that serves the
  region and product category, with a reason. When none serves them, staff MAY pick any active
  centre of the same company, with a reason.
- **FR-020**: Staff MUST be able to record the hand-over with the centre, the hand-over date, the
  centre's own job reference (optional) and a note (optional).
- **FR-021**: A claim MUST have at most one active repair centre at a time. A transfer MUST create
  a new hand-over record and keep the previous ones.
- **FR-022**: A repair centre, claim or policy of another company MUST be refused without revealing
  whether it exists, and the attempt MUST be recorded as a security event (001 FR-005).

#### Repair status

- **FR-023**: Each repair MUST have one status: Awaiting hand-over, At repair centre, Ready for
  collection, Collected, or Repair not possible (with the reason "failed" or "not economical").
- **FR-024**: Only these transitions MUST be allowed: Awaiting hand-over to At repair centre
  (hand-over recorded); At repair centre to Ready for collection; Ready for collection to
  Collected; At repair centre to Repair not possible; Repair not possible to Awaiting hand-over or
  At repair centre (new attempt, see FR-030); Awaiting hand-over or At repair centre to Awaiting
  hand-over at another centre (transfer). Every other transition MUST be refused with a message
  that names the current status and the allowed next steps.
- **FR-025**: Each status update MUST store the time the repair centre reported it, the time staff
  recorded it, the staff member, the source (phone, email or other) and an optional note. A
  reported time in the future, or before the hand-over, MUST be refused.
- **FR-026**: Claims agents and reviewers MAY record hand-overs and status updates. Auditors MUST
  NOT.
- **FR-027**: Staff MUST be able to correct a mistaken entry by recording a correction with a
  reason. The original entry MUST remain visible.
- **FR-028**: Repeated or simultaneous submissions of the same update MUST result in one change.
  Conflicting simultaneous updates MUST apply one and refuse the other with a reload message.

#### Failed and uneconomical repairs

- **FR-029**: Each repair attempt the repair centre reports as unsuccessful MUST count as one
  failed attempt for the claim. Transfers and "not economical" MUST NOT count.
- **FR-030**: After a failed attempt the policy MUST be evaluated again with the attempt count.
  Below the policy's limit, staff MUST be able to start a new attempt: at the same centre (status At
  repair centre) or after the product was returned to the customer (status Awaiting hand-over).
- **FR-031**: At the attempt limit, the policy's "repair failed" row MUST be recommended, and a
  reviewer MUST confirm the remedy change.
- **FR-032**: Staff MAY record a repair estimate reported by the centre. When the policy has an
  economic limit and the estimate is strictly above it, the repair MUST stop as "not economical",
  the "repair not economical" row MUST be recommended, and a reviewer MUST confirm the remedy.
- **FR-033**: A failed or uneconomical repair MUST NOT change the remedy by itself. The original
  remedy, every attempt with its result and reason, and every re-evaluation MUST remain in the
  history.

#### Replacement and refund

- **FR-034**: For a Replacement remedy, staff MUST be able to record "Replacement handed to
  customer" with the date, which completes the remedy.
- **FR-035**: For a Refund remedy, the refund MUST be carried out through the application's
  existing refund option. The claim MUST show when the refund was completed and its reference. This
  feature MUST NOT move money itself.

#### Protection plan

- **FR-036**: Claimants and claims agents MUST be able to state on a claim that the customer has an
  additional protection plan and attach the plan document as evidence.
- **FR-037**: A claim that relies on a protection plan to lift an exclusion MUST NOT be decided
  automatically; it MUST go to human review with the reason "protection plan to be checked". A
  stated plan without a plan document MUST lead to a request for the plan document as missing
  information (001 FR-010).

#### Customer communication

- **FR-038**: Entering Ready for collection MUST queue exactly one customer notification per repair
  attempt, with the centre's name, address and opening hours, through the existing simulated
  notification mechanism (001: nothing is sent externally in the PoC).
- **FR-039**: The claimant status page MUST show the remedy and its progress: for Repair, the
  timeline handed over, with repair centre, ready for collection, collected, plus the centre's
  name, address and opening hours; for Replacement and Refund, whether the remedy is completed.
  "With repair centre" MUST NOT state or imply that the repair is in progress.
- **FR-040**: While a remedy awaits a reviewer, the status page MUST say "We are deciding how to
  resolve your claim". After a failed repair it MUST say "Repair not possible. Your claim is being
  reviewed." until a new remedy or attempt is confirmed.
- **FR-041**: Claimant-facing text MUST NOT contain staff notes, staff names, repair-centre job
  references, failure reasons, risk signals or fraud indicators (001 FR-037).

#### Audit

- **FR-042**: Every remedy recommendation, remedy decision or change, repair-centre suggestion and
  choice, hand-over, transfer, status update, estimate, attempt, correction, remedy completion and
  queued notification MUST be appended to the claim's decision trail with actor, time, source, the
  policy version where relevant, and the previous and new value. Entries MUST NOT be changed or
  deleted (001 FR-039).
- **FR-043**: Reviewers and auditors MUST be able to see the remedy and repair history in the
  claim's decision trail in time order (001 FR-041).

#### Demonstration data

- **FR-044**: The PoC seed MUST give Aurora the Company A rules (covered defect: Repair; repair
  failed 2 times: Replacement; no economic limit; protection plan does not lift exclusions) and
  Borealis the Company B rules (covered defect: Repair; repair not economical above 60% of claim
  value: Refund or Replacement, reviewer chooses; repair failed 2 times: Refund; protection plan
  lifts the customer-damage exclusion, remedy Repair).
- **FR-045**: Each seeded company MUST have at least two active repair centres in each region it
  serves, at least one of which serves each of its product categories.
- **FR-046**: The seed MUST include, per company, scenarios that exercise every remedy table row,
  and at least one "same facts, different company" pair whose remedies differ.

### Key Entities

- **Remedy Table**: The remedy rules of one policy version: per scenario row, the one remedy or the
  list of allowed remedies; the failed-attempt limit; the economic limit; whether a protection plan
  lifts the customer-damage exclusion.
- **Remedy Decision**: The remedy of an approved claim, with how it was set (automatically by
  policy, or by a reviewer), the policy version and table row, the reason, who and when. Each
  change is a new decision; the current remedy is the latest one.
- **Repair Centre**: A company's repair location (name, region, product categories, address,
  opening hours, contact, active flag, priority). Extends the service centre of 001.
- **Repair**: The repair of one claim: current status, active repair centre, failed-attempt count.
- **Hand-over**: A record that a product is assigned to a repair centre: centre, hand-over date,
  centre job reference, note, recorded by. One is active at a time.
- **Repair Attempt**: One attempt at a centre and its result (ready, failed, not economical), with
  the reason and any estimate.
- **Repair Status Update**: One reported fact (received, estimate, ready, failed, collected,
  correction) with reported time, recorded time, source, staff member and note.
- **Remedy Completion**: For Replacement, "handed to customer" and the date; for Refund, the
  completion date and refund reference.
- **Protection Plan Claim**: The customer's statement that a plan exists, its document, and the
  reviewer's confirmation.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Staff record a hand-over, or a repair status update, from the claim page in under one
  minute.
- **SC-002**: In 100% of the seeded remedy scenarios, each company gets the remedy its own policy
  gives, through the same decision process, and every "same facts, different company" pair yields
  the different remedies the two policies dictate.
- **SC-003**: Zero remedies outside the policy's allowed list are recommended or accepted across the
  test set; 100% of such attempts are refused.
- **SC-004**: Exactly one customer notification is queued per repair attempt that reaches Ready for
  collection, including when the action is repeated or performed by two staff members at once.
- **SC-005**: The claimant sees "Ready for collection" and the collection details the next time they
  open or refresh the status page after staff record it.
- **SC-006**: 100% of remedy and repair events appear in the decision trail with actor, time,
  source and, where relevant, policy version; an auditor unfamiliar with the claim can explain from
  the trail alone why the claim has its remedy in under 3 minutes.
- **SC-007**: Zero cross-tenant exposures of repair centres, repairs, remedies or policies; 100% of
  deliberate cross-tenant attempts are refused and recorded as security events.
- **SC-008**: 100% of failed or uneconomical repairs end in either a new attempt or a
  reviewer-confirmed remedy; none changes the remedy without a reviewer.
- **SC-009**: Every acceptance scenario of feature 001 still passes; no existing claim outcome
  changes.

## Assumptions

- **Scope is coordination.** Diagnosis, parts, labour, technician scheduling, repair invoices and
  repair costs (beyond a reported estimate) stay in the repair centre's own software.
- **No integration with repair-centre software** in this feature. All repair-centre information is
  keyed in by staff. A later integration would feed the same status updates.
- **Refund** is carried out by the application's existing refund option; this feature records the
  remedy and its completion only.
- **Replacement stock, ordering and shipping**, courier and pickup booking, and repair SLAs,
  reminders and escalation are out of scope.
- **Repair centres and remedy rules are seeded.** There is no repair-centre onboarding screen and no
  policy editor.
- **Seeded policy versions are demonstration data.** The remedy tables are added to the existing
  seeded policy versions of Aurora and Borealis, because a new overlapping version would make the
  applicable version ambiguous (001 edge cases). Borealis keeps its existing included
  accidental-damage incident; the protection plan covers customer-caused damage beyond it.
- **This feature replaces the simulated repair request of 001** for claims approved after it is
  live; the existing approval notification stays.
- **Selling or issuing protection plans** is out of scope; the plan is proven by the customer's
  document and a reviewer's check.
- **Notifications stay simulated** as in 001; nothing is sent by email or SMS.
- **The economic limit** of 60% for Borealis is a PoC placeholder chosen during team review.
