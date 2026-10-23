# UI Contract: Replacement Fulfilment

**Feature**: `specs/003-replacement-fulfilment` | **Date**: 2026-10-23 | **Plan**: [../plan.md](../plan.md)

Binding for every `src/web` task of this feature. It extends
[002 ui-screens.md](../../002-remedy-repair-handover/contracts/ui-screens.md) and through it
[001 ui-design.md](../../001-ai-claim-adjudication/ui-design.md): same tokens, component kit,
presentation mappings and copy rules. Nothing here adds a token or a UI library.

## 1. Visual language

As 002 §1: no AI styling on any replacement element. Policy-derived values (product rule, return
rule, "not available" recommendation) use grey system ink with the policy code and version;
reviewer approvals of a model use blue human ink with name and time; location reports use blue
human ink with source and both times.

New presentation mappings in `src/web/src/shared/presentation/`:

| Value | Label | Tone |
|-------|-------|------|
| Replacement `AwaitingReturn` | Awaiting return | `pending` |
| Replacement `AwaitingUnit` | Awaiting replacement unit | `pending` |
| Replacement `ReadyForCollection` | Ready for collection | `ok` |
| Replacement `HandedToCustomer` | Handed to customer | `ok` |
| Replacement `NotAvailable` | Replacement not available | `err` |
| Replacement `Cancelled` | Cancelled — remedy changed | `system` |
| Return `Mismatch` | Returned product does not match | `warn` |
| Proposal `Pending` | Awaiting model approval | `warn` |
| Product rule `SameModel` / `SameCategoryAtLeastClaimValue` | Same model only / Same model or approved equivalent | `system` |
| Return rule `AtCollection` / `BeforeReservation` | Returned at collection / Returned before reservation | `system` |

## 2. Staff claim workspace — Remedy tab

The 002 **Completion card** for Replacement is replaced by a **Replacement card** (left column),
shown when `ClaimDetail.replacement` is present:

- Status badge; active location (name, address, opening hours, staff-only contact); policy basis
  line "AUR-WP v2 · Same model or approved equivalent · Returned at collection".
- Return line: state badge, date, serial seen (mono) with a `warn` badge when it does not match the
  claim's serial; "Held by repair centre — [centre name]" when FR-020 applied.
- Model line: claimed model, approved model if any, pending proposal with reason, proposer and
  time.
- Current unit: model and serial (mono).
- Action buttons rendered **only from `ReplacementView.allowedEvents`**: Change location · Record
  return · Propose other model · Approve model / Decline model (reviewers) · Accept return
  (reviewers) · Reserve unit (primary when allowed) · Customer refused unit · Record hand-over to
  customer (primary when ready) · Not available · Move · Correct an entry. One primary button per
  view.
- Each button opens one `Dialog` with the fields of `ReplacementEventRequest` for that type:
  reported date-time (default now, not in the future), source, the event's fields, optional note.
  Reason fields use `CharacterCount` 10–1000. **Propose other model** lists only
  `GET /replacement-models`. **Record hand-over to customer** shows a required checkbox "Faulty
  product returned" under `AtCollection` unless already returned or held. Submit sends `If-Match`
  with the replacement ETag.
- Errors as 002 §2 (409 message with allowed actions; 412 with **Reload**; 403 message for
  reviewer-only actions).

**Right — Replacement events**: `Timeline` of `ReplacementView.events`, corrections linked and the
corrected entry struck through, never hidden.

Auditors see everything read-only.

## 3. Review queue

`/staff/review` adds items of kind `ReplacementCheck` with the badge "Replacement" and the reason
("Model approval" or "Returned product does not match"). Selecting one opens the claim's Remedy tab
with the Replacement card focused. `REPLACEMENT_NOT_AVAILABLE` remedy items use the 002 Remedy item
with the reason "No replacement available; the policy recommends Refund."

## 4. Claimant portal — status page

For a Replacement remedy, the 002 remedy section shows:

- Remedy line "Remedy: Replacement" and the label from data-model.md "Claimant projection".
- `AwaitingReturn`: a card "Please bring your product to" with the location's name, address and
  opening hours.
- A `Timeline` from `replacementSteps` (Product returned, when it applies · Ready for collection ·
  Handed to you) and the location card. When ready, the card becomes an `ok` callout "Your
  replacement is ready for collection" with `replacementModelName` and, under `AtCollection`,
  "Please bring your faulty product with you."
- `NotAvailable` and pending decisions: a neutral `pending` banner with the label text only.
- Never shows serial numbers, notes, reasons, return check results or staff names (FR-034).

## 5. Copy

- "location" in staff strings, "replacement location" only where the context is ambiguous;
  claimants see the location's name, not a type.
- Buttons state the action: "Reserve unit", "Record hand-over to customer", "Record return".
- Never write "shipped", "on its way" or "delivered": replacements are collected (clarification
  2026-10-23). The 002 claimant label "Replacement on its way" is replaced by the labels above.
