# Feature Specification: Replacement Fulfilment and Faulty-Product Return

**Feature Branch**: `docs/003-replacement-spec` (proposed) — spec directory `specs/003-replacement-fulfilment`

**Created**: 2026-10-23

**Status**: Draft

**Input**: User description: "Currently the application supports only refunds, and we created a
spec for repairs. Now we need to add replacement as well: go through the application and create a
spec for replacements as we did for repairs."

**Builds on**: `specs/001-ai-claim-adjudication` and `specs/002-remedy-repair-handover`. References
of the form "001 FR-017" or "002 FR-034" point to those specifications. Feature 002 decides *when* a
claim's remedy is Replacement (policy remedy table, reviewer confirmation) and records only that
the replacement was handed to the customer (002 FR-034). This feature defines *how* a replacement
is carried out and tracked, and replaces 002 FR-034 with that process. How the remedy is decided
(002 FR-001 to FR-016) does not change.

## Clarifications

### Session 2026-10-23 (clarification)

- Q: How does the customer get the replacement product? → A: Collection only. The customer
  collects it at one of the company's locations, chosen the same way as a repair centre: the
  system suggests one, staff may pick another eligible location of the same company. Delivery and
  shipping stay out of scope.
- Q: When must the customer give back the faulty product, and do the companies differ? → A: The
  policy decides. Aurora: handed back when collecting the replacement. Borealis: returned before
  a replacement unit is reserved.
- Q: When the same model is not available, what may be handed out instead, and who decides? → A:
  The policy decides. Aurora: the same model, or a model of the same product category with at
  least the same claim value, approved by a reviewer. Borealis: the same model only.
- Q: When no allowed replacement is available, what remedy does each company's policy recommend?
  → A: Refund at both companies, through the "replacement not available" row; a reviewer confirms
  the change.
- Q: How long is a replacement unit covered by warranty? → A: Aurora: the remainder of the
  original warranty. Borealis: the remainder of the original warranty, at least 6 months from the
  hand-over to the customer.

### Session 2026-10-23 (carried over from the 002 team review)

These answers apply decisions of the 002 team review to replacements; they were not asked again.

- Q: How does WARRANTY-AI learn that a replacement unit is available? → A: As for repairs, the
  location's own software is not connected. The location tells staff by phone or email that a unit
  is reserved for the customer, and staff record it. Stock and ordering stay outside the system.
- Q: Who may record replacement steps? → A: The same roles as for repairs: claims agents and
  reviewers record the location's updates; only reviewers approve a different model or change a
  remedy; auditors are read-only.

## User Scenarios & Testing *(mandatory)*

### Actors

- **Claimant** — the end customer. Returns the faulty product, collects the replacement, and
  follows the claim on the claim status page.
- **Claims Agent** — a tenant staff member who records the location's updates on the claim.
- **Claims Reviewer** — a tenant staff member who also approves a different replacement model and
  confirms or changes remedies.
- **Auditor** — a tenant staff member who inspects the replacement history (read-only).
- **Replacement Location** — a location of the tenant that hands out replacement units. These are
  the tenant's repair centres (002 FR-017) that are marked as issuing replacements. It keeps its
  own software and only reports facts: faulty product received, unit reserved (model and serial),
  no unit available, unit handed to customer, unit refused by customer.
- **Tenant (company)** — configures its own replacement rules in its warranty policy.

Every staff user belongs to exactly one tenant (001 FR-005). A reviewer may never approve a model
or change the remedy of a claim they submitted (001 FR-034).

### Where replacement fits in the shared decision process

The shared decision process of 002 is unchanged. A claim's remedy becomes Replacement in step 3 or
4 of that process: directly from the covered-defect row, after failed repairs (002 FR-031), or by a
reviewer's choice (002 FR-011). From that moment, this feature applies:

1. Which location hands out the replacement? Suggested from the company's locations (new).
2. Must the faulty product come back first? Taken from the company's policy (new).
3. Which product may be handed out? Same model, or an allowed equivalent approved by a reviewer
   (new).
4. If no allowed product is available, step 3 of the 002 process is evaluated again with the
   "replacement not available" row (new).

Example used for the PoC seed (continuing the 002 example):

| Scenario | Company A (seeded as Aurora) | Company B (seeded as Borealis) |
|---|---|---|
| Replacement product allowed | Same model, or a model of the same category with at least the same claim value (reviewer approves) | Same model only |
| Faulty product return | Handed back when collecting the replacement | Returned before the replacement is reserved |
| No allowed replacement available | Refund | Refund |
| Cover of the replacement unit | Remainder of the original warranty | Remainder of the original warranty, at least 6 months |

### User Story 1 - Replacement collection and customer notification (Priority: P1)

For a claim whose remedy is Replacement, the system suggests a replacement location of the same
company for the claim's region and product category. Staff confirm it or pick another eligible
location. The customer sees on the claim status page where the replacement will be handed out.
When the location tells staff it has reserved a unit, staff record the model and serial number of
that unit and mark the replacement ready for collection; the customer is notified once. When the
customer collects it, staff record the hand-over, including the return of the faulty product where
the policy asks for it then, and the remedy is completed.

**Why this priority**: It is the replacement counterpart of the repair hand-over in 002 and the
coordination the business is missing: knowing whether a unit is waiting for the customer, which
unit it is, and telling the customer when to come. On its own it delivers the full happy path for
a same-model replacement.

**Independent Test**: Take an approved Aurora claim with remedy Replacement through suggested
location, unit reserved, ready for collection and handed to customer; verify each step on the claim
page and in the decision trail, exactly one customer notification, the recorded replacement serial
number, the faulty product recorded as returned at collection, and the claimant status page at each
step.

**Acceptance Scenarios**:

1. **Given** a claim whose remedy becomes Replacement, **When** the remedy is set, **Then** the
   replacement status is "Awaiting replacement unit" (or "Awaiting return", see User Story 2) and
   an eligible replacement location is suggested.
2. **Given** a suggested location, **When** staff pick another active replacement location of the
   same company that serves the region and product category, **Then** it becomes the claim's
   location and the change is recorded with the staff member and a reason.
3. **Given** "Awaiting replacement unit", **When** staff record that the location has reserved a
   unit of the same model with its serial number, **Then** the status becomes "Ready for
   collection", one customer notification is queued with the location's name, address and opening
   hours, and the claimant status page shows the same.
4. **Given** "Ready for collection", **When** staff mark it ready again, or two staff members record
   the reserved unit at the same time, **Then** there is one status change and one notification.
5. **Given** "Ready for collection" at a company whose policy takes the faulty product back at
   collection, **When** staff record the hand-over, **Then** they must confirm that the faulty
   product was returned; the status becomes "Handed to customer" and the remedy is completed.
6. **Given** "Handed to customer", **When** staff try any further status change, **Then** it is
   refused.
7. **Given** "Awaiting replacement unit", **When** staff try to record the hand-over to the
   customer, **Then** it is refused with the message that a unit must be reserved first.
8. **Given** the claimant opens the status page, **When** the replacement is in any state, **Then**
   they see the remedy and the timeline (faulty product returned where it applies, replacement
   ready for collection, handed to you) with the location's name, address and opening hours, and no
   staff notes, staff names, serial numbers of other units or risk information.

---

### User Story 2 - Return of the faulty product as the policy requires (Priority: P2)

A company's policy says when the customer gives back the faulty product: in exchange at
collection (User Story 1), or before a replacement unit is reserved. For a return-first company,
the replacement starts at "Awaiting return": the customer brings the faulty product to the
location, staff record its receipt, and only then is a unit reserved. When the faulty product is
already with the company, for example because it is at a repair centre after failed repairs, no
second return is asked of the customer.

**Why this priority**: It is a real policy difference between the two companies and protects the
companies against a customer keeping both products, but the happy path in User Story 1 works
without it.

**Independent Test**: Take an approved Borealis claim with remedy Replacement; verify it starts at
"Awaiting return", that a unit cannot be reserved before the return is recorded, and that the
claimant status page tells the customer where to bring the product. Take an Aurora claim whose
Replacement follows two failed repairs; verify the faulty product is recorded as already with the
repair centre and the customer is not asked to bring it.

**Acceptance Scenarios**:

1. **Given** a Borealis claim whose remedy becomes Replacement, **When** the remedy is set,
   **Then** the replacement status is "Awaiting return" and the status page asks the customer to
   bring the faulty product to the suggested location.
2. **Given** "Awaiting return", **When** staff try to record a reserved unit, **Then** it is
   refused with the message that the faulty product must be returned first.
3. **Given** "Awaiting return", **When** staff record the return with the date, the serial number
   seen on the returned product and an optional note, **Then** the status becomes "Awaiting
   replacement unit".
4. **Given** a returned product whose serial number differs from the claim's serial number,
   **When** staff record the return, **Then** the return is stored with a mismatch flag, the
   replacement does not proceed, and the claim goes to the reviewers' queue with the reason
   "returned product does not match the claim".
5. **Given** a Replacement that follows a repair whose product is still at a repair centre of the
   same company, **When** the remedy becomes Replacement, **Then** the faulty product is recorded as
   already returned (with a reference to the repair hand-over) and the customer is not asked to
   bring it.
6. **Given** the claimant opens the status page while "Awaiting return", **When** it loads,
   **Then** they see "Please bring your product to …" with the location's name, address and
   opening hours.

---

### User Story 3 - Equivalent product and replacement not available (Priority: P2)

The location may report that the same model is no longer available. Where the policy allows an
equivalent, staff propose another model of the same product category and a reviewer approves it
before it is reserved. Where the policy allows no equivalent, or none is available, staff record
"Replacement not available"; the policy is evaluated again with its "replacement not available"
row, and a reviewer confirms the new remedy. The original remedy and every step remain in the
history; the claim stays Approved.

**Why this priority**: Without it, a discontinued or out-of-stock model is a dead end. It is the
replacement counterpart of the failed-repair re-evaluation in 002, but it is only needed once
replacements are tracked (User Story 1).

**Independent Test**: On an Aurora claim, propose a model of the same category with a higher claim
value; verify it cannot be reserved until a reviewer approves it, and that a model of a lower claim
value or another category is refused. On a Borealis claim, propose another model; verify it is
refused because the policy allows the same model only. Record "Replacement not available" on both;
verify Refund is recommended and a reviewer must confirm it.

**Acceptance Scenarios**:

1. **Given** an Aurora claim "Awaiting replacement unit", **When** staff propose another model of
   the same product category whose claim value is at least the original's, with a reason, **Then**
   the status stays "Awaiting replacement unit", the proposal shows "Awaiting model approval", and
   the claim appears in the reviewers' queue.
2. **Given** a pending model proposal, **When** a reviewer approves it, **Then** staff may record a
   reserved unit of that model; **When** the reviewer declines it with a reason, **Then** only the
   same model can be reserved again.
3. **Given** any claim, **When** staff propose a model of another product category or of a lower
   claim value, **Then** it is refused with a message naming the rule of the policy.
4. **Given** a Borealis claim, **When** staff propose any model other than the claimed one, **Then**
   it is refused because the policy allows the same model only.
5. **Given** "Awaiting replacement unit", **When** staff record "Replacement not available" with a
   reason, **Then** the status becomes "Replacement not available", the policy is evaluated again
   with the "replacement not available" row, and the remedy shows "Awaiting remedy decision" until a
   reviewer confirms the recommended remedy (Refund at both seeded companies).
6. **Given** a confirmed remedy change from Replacement to Refund, **When** the history is opened,
   **Then** it shows the original Replacement remedy, every location and model step and its result,
   the policy version used and the reviewer's reason; the faulty product's return status stays on
   the claim.
7. **Given** the claimant opens the status page after "Replacement not available", **When** no new
   remedy is confirmed yet, **Then** they see "Replacement not possible. Your claim is being
   reviewed." without the internal reason.

---

### User Story 4 - Warranty of the replacement unit (Priority: P3)

The replacement unit is covered for the period the company's policy gives a replacement. Its
serial number is linked to the original purchase. When the customer later claims for the
replacement unit, the claim is recognised as a replacement, uses the original purchase details,
and its coverage is computed from the replacement cover. A later claim for the returned faulty
unit is flagged.

**Why this priority**: It closes the loop for real operation and prevents false risk signals on
later claims, but it is not needed to demonstrate the replacement itself.

**Independent Test**: Complete an Aurora replacement and a Borealis replacement; submit a new claim
for each replacement serial number; verify both are recognised as replacement units, are not
flagged as "serial not in catalog", and that Borealis gives at least 6 months of cover where the
original warranty would already have ended. Submit a claim for the returned faulty serial; verify
it is flagged as a risk signal.

**Acceptance Scenarios**:

1. **Given** a replacement handed to the customer, **When** staff recorded its serial number,
   **Then** the unit is registered for the tenant as a replacement of the claim's product, linked
   to the original purchase.
2. **Given** a new claim for a registered replacement serial number, **When** it is evaluated,
   **Then** "product/serial not in the tenant's catalog" (001 FR-017) is not raised for it, and its
   coverage end is the later of the original warranty end and the policy's minimum replacement
   cover from the hand-over date.
3. **Given** a new claim for a serial number that was returned as faulty and replaced, **When** it
   is evaluated, **Then** it carries the risk signal "product was returned and replaced" and goes to
   human review.
4. **Given** a replacement unit's serial number recorded at another company, **When** a claim for it
   is submitted, **Then** the other company's record is never consulted (001 FR-018).

---

### User Story 5 - Location change, refused unit and corrections (Priority: P3)

A replacement sometimes has to move to another location, the customer may refuse a unit that is
damaged or wrong at collection, or staff record something by mistake. Staff can move the
replacement to another eligible location, record a refused unit with a reason, and correct a
mistaken entry with a reason. Nothing is overwritten.

**Why this priority**: Needed for real-world operation but not for the core demonstration.

**Independent Test**: Move a "Ready for collection" replacement to another location; verify one
active location, the old one and its reserved unit in the history, and a new notification only
once the new location reserves a unit. Record a refused unit; verify the status returns to
"Awaiting replacement unit" and the remedy is unchanged. Correct a mistaken serial number; verify
the original and the correction both appear.

**Acceptance Scenarios**:

1. **Given** "Awaiting return", "Awaiting replacement unit" or "Ready for collection", **When**
   staff move the replacement to another eligible location with a reason, **Then** the new location
   becomes the only active location, the old one and any unit reserved there stay in the history,
   and the status becomes "Awaiting replacement unit" (or stays "Awaiting return").
2. **Given** "Ready for collection", **When** staff record that the customer refused the unit with
   a reason (for example damaged, wrong model, does not work), **Then** the status becomes
   "Awaiting replacement unit", the refused unit stays in the history, and the remedy does not
   change.
3. **Given** a new unit reserved after a refusal or a move, **When** it is marked ready, **Then**
   one new customer notification is queued for that unit.
4. **Given** a mistaken entry, **When** staff record a correction with a reason, **Then** the
   corrected value applies, and both the original entry and the correction remain visible in the
   history.
5. **Given** a location of another company, **When** staff try to move the replacement to it,
   **Then** it is refused and recorded as a security event.

---

### Edge Cases

- **No eligible replacement location** for the claim's region and product category: no location is
  suggested; staff may pick any active replacement location of the same company with a reason, or
  a reviewer may change the remedy to another remedy the policy allows.
- **Policy version without replacement rules** (for example a version from before this feature):
  the replacement proceeds with the safest defaults — same model only, faulty product returned at
  collection — and "Replacement not available" goes to manual review with the reason "policy gives
  no remedy for this scenario" (002 FR-012).
- **Replacements decided before this feature went live** (recorded with the 002 completion step
  only): they keep their records; no replacement tracking is created for them retroactively.
- **Equivalent model with exactly the original claim value**: allowed where the policy allows
  equivalents.
- **Product model without a claim value** (001 edge cases): an equivalent model cannot be compared;
  only the same model may be reserved.
- **Remedy changes from Replacement to Repair or Refund while a unit is reserved**: the replacement
  ends as "Cancelled — remedy changed"; the reserved unit stays in the history; no further
  notification is queued for it.
- **Faulty product already returned, then the remedy changes to Refund**: the return stays
  recorded; this feature does not give the product back.
- **Customer does not collect the replacement**: the status stays "Ready for collection"; reminders
  and expiry are out of scope.
- **Two staff members update the same replacement at the same time**: one update applies; the other
  is refused with a message that the claim changed and must be reloaded.
- **Reported time in the future, before the remedy was set, or a hand-over before the unit was
  reserved**: refused with a clear message.
- **Serial number of the reserved unit equals the faulty unit's serial number, or is already
  registered as a unit of another claim in the same company**: refused with a clear message.
- **Reviewer tries to approve a model for, or change the remedy of, a claim they submitted**:
  refused and recorded as a security event (as 001 FR-034).
- **Claimant opens the status page while a model proposal or a remedy decision is pending**: sees
  "We are preparing your replacement" or "We are deciding how to resolve your claim" and no
  internal details.
- **Correction of "Ready for collection" back to "Awaiting replacement unit"**: allowed with a
  reason; the notification already queued is not withdrawn, and no second notification is queued
  for the same unit.
- **Cross-tenant references** (location, claim, serial number or policy of another company) in any
  request: refused without revealing whether the target exists, and recorded as a security event.

## Requirements *(mandatory)*

### Functional Requirements

#### Company replacement rules

- **FR-001**: Replacement rules MUST come from the claim's company policy, never from a
  platform-wide rule. Each policy version MUST be able to hold, next to its 002 remedy table:
  which products may be handed out as a replacement; when the faulty product must be returned; the
  minimum cover of a replacement unit; and a "replacement not available" remedy row.
- **FR-002**: The product rule MUST be one of: same model only; or same model, or another model of
  the same product category whose claim value (001 FR-001) is at least the original's, approved by
  a reviewer.
- **FR-003**: The return rule MUST be one of: faulty product returned at collection of the
  replacement; or faulty product returned before a replacement unit is reserved.
- **FR-004**: The "replacement not available" row MUST follow the 002 remedy row rules (002
  FR-002, FR-003): exactly one remedy or a list of allowed remedies, never a remedy outside the
  three remedy types.
- **FR-005**: Replacement rules MUST be part of the policy version and selected with it (002
  FR-007). Once a version is in use its replacement rules MUST NOT change.
- **FR-006**: All companies MUST go through the same replacement process; company differences MUST
  come only from policy data (002 FR-008).

#### Replacement locations

- **FR-007**: A company's replacement locations MUST be its repair centres (002 FR-017) that are
  marked as issuing replacements, not a separate list.
- **FR-008**: For a Replacement remedy, the system MUST suggest the active replacement location of
  the claim's company that serves the claim's region and product category, choosing the highest
  priority and then the name, as 002 FR-018. A suggestion MUST NOT count as a reserved unit.
- **FR-009**: Staff MUST be able to pick or move to another active replacement location of the same
  company that serves the region and product category, with a reason. When none serves them, staff
  MAY pick any active replacement location of the same company, with a reason.
- **FR-010**: A replacement MUST have at most one active location at a time. A move MUST keep the
  previous location and any unit reserved there in the history.
- **FR-011**: A location, claim, serial number or policy of another company MUST be refused without
  revealing whether it exists, and the attempt MUST be recorded as a security event (001 FR-005).

#### Replacement status

- **FR-012**: Each replacement MUST have one status: Awaiting return, Awaiting replacement unit,
  Ready for collection, Handed to customer, Replacement not available, or Cancelled — remedy
  changed.
- **FR-013**: A replacement MUST start at "Awaiting return" when the policy requires the return
  before a unit is reserved and the faulty product is not already with the company (FR-020);
  otherwise at "Awaiting replacement unit".
- **FR-014**: Only these transitions MUST be allowed: Awaiting return to Awaiting replacement unit
  (return recorded); Awaiting replacement unit to Ready for collection (unit reserved); Ready for
  collection to Handed to customer; Ready for collection to Awaiting replacement unit (unit refused
  or location moved); Awaiting return or Awaiting replacement unit to the same status at another
  location (moved); Awaiting replacement unit to Replacement not available; any status except
  Handed to customer to Cancelled — remedy changed (only by a reviewer's remedy change). Every other
  transition MUST be refused with a message that names the current status and the allowed next
  steps.
- **FR-015**: Each status update MUST store the time the location reported it, the time staff
  recorded it, the staff member, the source (phone, email, in person or other) and an optional
  note. A reported time in the future, or before the step it follows, MUST be refused.
- **FR-016**: Claims agents and reviewers MAY record replacement updates. Auditors MUST NOT.
- **FR-017**: Staff MUST be able to correct a mistaken entry by recording a correction with a
  reason. The original entry MUST remain visible.
- **FR-018**: Repeated or simultaneous submissions of the same update MUST result in one change.
  Conflicting simultaneous updates MUST apply one and refuse the other with a reload message.

#### Faulty product return

- **FR-019**: Staff MUST be able to record the return of the faulty product with the date, the
  location, the serial number seen on the returned product and an optional note. A serial number
  that does not match the claim's serial number (after the normalisation of 001 FR-016) MUST stop
  the replacement and send the claim to the reviewers' queue with the reason "returned product does
  not match the claim".
- **FR-020**: When the faulty product is already with a repair centre of the same company under an
  active or finished repair of the same claim (002), the return MUST be recorded automatically,
  referring to that repair hand-over, and the customer MUST NOT be asked to bring it.
- **FR-021**: Under the "returned at collection" rule, recording the hand-over to the customer MUST
  require the confirmation that the faulty product was returned, unless FR-020 already applies. A
  returned product that does not match the claim (FR-019) MUST block the hand-over until a reviewer
  accepts the return.

#### Replacement unit

- **FR-022**: Staff MUST be able to record a reserved unit with its model and serial number. The
  model MUST be the claimed model or a model approved for this claim (FR-024). The serial number
  MUST NOT be the faulty unit's, MUST NOT have been handed out before as a replacement, and MUST NOT
  be the serial of any claim of the same company. When the company's catalog already lists the
  serial, it MUST belong to the reserved model.
- **FR-023**: Staff MAY propose another model with a reason when the policy allows equivalents. A
  proposed model of another product category, of a lower claim value, or without a claim value MUST
  be refused.
- **FR-024**: Only reviewers MAY approve or decline a proposed model, with a reason for a decline.
  A pending proposal MUST appear in the reviewers' queue. AI components MUST NOT propose, approve or
  choose a replacement model.
- **FR-025**: Entering Handed to customer MUST complete the claim's remedy (replacing 002 FR-034)
  and MUST register the handed-out unit's serial number for the tenant as a replacement of the
  claim's product, linked to the original purchase and the claim.

#### Replacement not available

- **FR-026**: Staff MUST be able to record "Replacement not available" with a reason. The policy
  MUST then be evaluated again with the "replacement not available" row, and the recommended remedy
  MUST be confirmed by a reviewer (002 FR-015), as for a failed repair (002 FR-031).
- **FR-027**: "Replacement not available", a refused unit or a move MUST NOT change the remedy by
  itself. The original remedy, every location, proposal, reserved unit, refusal and re-evaluation
  MUST remain in the history.
- **FR-028**: When a reviewer changes the remedy away from Replacement, the replacement MUST end as
  "Cancelled — remedy changed" and no further notification MUST be queued for it.

#### Replacement unit warranty

- **FR-029**: A claim for a registered replacement serial number MUST be recognised as a
  replacement unit: the "product/serial not in the tenant's catalog" signal (001 FR-017) MUST NOT be
  raised for it, its purchase details MUST come from the original purchase, and its coverage end
  MUST be the later of the original warranty end and the hand-over date plus the policy's minimum
  replacement cover.
- **FR-030**: A claim for a serial number that was returned as faulty and replaced MUST carry the
  risk signal "product was returned and replaced" and MUST go to human review.

#### Customer communication

- **FR-031**: Entering Ready for collection MUST queue exactly one customer notification per
  reserved unit, with the location's name, address and opening hours, through the existing
  simulated notification mechanism (001: nothing is sent externally in the PoC).
- **FR-032**: The claimant status page MUST show the replacement and its progress: "Please bring
  your product to …" while awaiting return, then the timeline faulty product returned (where it
  applies), replacement ready for collection, handed to you, with the location's name, address and
  opening hours.
- **FR-033**: While a model proposal is pending, the status page MUST say "We are preparing your
  replacement". After "Replacement not available" it MUST say "Replacement not possible. Your claim
  is being reviewed." until a new remedy is confirmed. While a remedy awaits a reviewer, 002 FR-040
  applies.
- **FR-034**: Claimant-facing text MUST NOT contain staff notes, staff names, the returned
  product's serial check result, reasons for unavailability or refusal, risk signals or fraud
  indicators (001 FR-037, 002 FR-041). It MAY show the model of the replacement unit.

#### Audit

- **FR-035**: Every location suggestion and choice, move, return, model proposal and its approval or
  decline, reserved unit, ready, refusal, hand-over to customer, "replacement not available",
  re-evaluation, cancellation, correction, serial registration and queued notification MUST be
  appended to the claim's decision trail with actor, time, source, the policy version where
  relevant, and the previous and new value. Entries MUST NOT be changed or deleted (001 FR-039).
- **FR-036**: Reviewers and auditors MUST be able to see the replacement history in the claim's
  decision trail in time order, together with the remedy history of 002 (001 FR-041).

#### Demonstration data

- **FR-037**: The PoC seed MUST give Aurora the Company A replacement rules (same model, or a model
  of the same category with at least the same claim value, reviewer approves; faulty product
  returned at collection; replacement not available: Refund; replacement cover: remainder of the
  original warranty) and Borealis the Company B rules (same model only; faulty product returned
  before a unit is reserved; replacement not available: Refund; replacement cover: remainder of the
  original warranty, at least 6 months).
- **FR-038**: Each seeded company MUST have at least one active replacement location in each region
  it serves for each of its product categories, and at least one product category with two models
  so that an equivalent can be proposed.
- **FR-039**: The seed MUST include, per company, scenarios that reach Replacement (directly or
  after failed repairs), that exercise each return rule, "replacement not available" and, for
  Aurora, an approved equivalent model; and at least one "same facts, different company" pair whose
  replacement steps differ.

### Key Entities

- **Replacement Rules**: The replacement part of one policy version: product rule, return rule,
  minimum replacement cover and the "replacement not available" remedy row. Extends the 002 Remedy
  Table.
- **Replacement Location**: A repair centre of the company (002) that issues replacements, with the
  same name, region, product categories, address, opening hours, contact, active flag and priority.
- **Replacement**: The replacement of one claim: current status, active location, return state,
  current reserved unit.
- **Location Assignment**: A record that a replacement is assigned to a location: how it was
  assigned (suggested, chosen by staff, moved), reason, recorded by. One is active at a time.
- **Faulty Product Return**: The return of the claimed product: date, location, serial number seen,
  match result, or a reference to the repair hand-over that already holds it.
- **Model Proposal**: A proposed other model, its reason, and the reviewer's approval or decline.
- **Replacement Unit**: A unit reserved for the customer: model, serial number, location, and its
  result (handed over, refused, cancelled), with the reason.
- **Replacement Status Update**: One reported fact (returned, reserved, ready, refused, handed over,
  not available, moved, correction) with reported time, recorded time, source, staff member and
  note.
- **Registered Replacement Serial**: The link between a handed-out serial number, the claim, the
  original purchase and the replacement cover end.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Staff record a return, a reserved unit or a hand-over to the customer from the claim
  page in under one minute.
- **SC-002**: In 100% of the seeded replacement scenarios, each company follows its own product
  rule, return rule and "replacement not available" row through the same process, and every "same
  facts, different company" pair shows the different steps the two policies dictate.
- **SC-003**: Zero replacement units of a model the policy does not allow, or not approved by a
  reviewer, are accepted across the test set; 100% of such attempts are refused.
- **SC-004**: Zero replacements are handed out under a return-first policy before the faulty
  product's return is recorded.
- **SC-005**: Exactly one customer notification is queued per reserved unit that reaches Ready for
  collection, including when the action is repeated or performed by two staff members at once.
- **SC-006**: The claimant sees "Ready for collection" and the collection details the next time they
  open or refresh the status page after staff record it.
- **SC-007**: 100% of replacement events appear in the decision trail with actor, time, source and,
  where relevant, policy version; an auditor unfamiliar with the claim can explain from the trail
  alone which unit the customer received and why in under 3 minutes.
- **SC-008**: 100% of "Replacement not available" outcomes end in a reviewer-confirmed remedy; none
  changes the remedy without a reviewer.
- **SC-009**: 100% of later claims for a registered replacement serial number are recognised as
  replacements without a "not in catalog" signal; 100% of later claims for a returned faulty serial
  number are flagged and go to human review.
- **SC-010**: Zero cross-tenant exposures of locations, replacements, serial numbers or policies;
  100% of deliberate cross-tenant attempts are refused and recorded as security events.
- **SC-011**: Every acceptance scenario of features 001 and 002 still passes, except 002 User
  Story 4 scenario 1, which this feature replaces; no existing claim outcome changes.

## Assumptions

- **Feature 002 comes first.** This feature depends on the remedy decision, remedy table, repair
  centres and repair hand-over of 002, which are specified but not yet implemented. It should be
  planned after 002's plan is approved and implemented after 002's remedy and repair stories.
- **Scope is coordination.** Stock levels, ordering, purchasing, refurbishment and disposal of the
  returned product stay in the company's and the location's own software.
- **No integration with location software.** All location information is keyed in by staff, as
  for repairs (002). A later integration would feed the same updates.
- **Collection only.** Delivery, shipping, couriers and pickup booking are out of scope; the
  customer brings and collects products at a location.
- **Refund** stays as specified in 002 FR-035 (existing refund option). This feature only leads to
  a Refund recommendation through the "replacement not available" row.
- **Locations and replacement rules are seeded.** There is no location onboarding screen and no
  policy editor. The rules are added to the existing seeded policy versions of Aurora and Borealis,
  as in 002, and the seeded clause text stays unchanged.
- **The 6-month minimum replacement cover** for Borealis is a PoC figure confirmed in
  clarification, like the 60% economic limit of 002; a real company would set its own value in its
  policy.
- **Claim value** is the per-model catalog value of 001 FR-001; it is used only to compare models,
  not as a price paid or charged.
- **Notifications stay simulated** as in 001; nothing is sent by email or SMS.
- **Reminders, expiry of uncollected replacements and replacement SLAs** are out of scope.
