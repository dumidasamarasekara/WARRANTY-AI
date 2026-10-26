# UI Contract: Remedies and Repair Hand-over

**Feature**: `specs/002-remedy-repair-handover` | **Date**: 2026-10-14 | **Plan**: [../plan.md](../plan.md)

Binding for every `src/web` task of this feature. It extends
[001 ui-design.md](../../001-ai-claim-adjudication/ui-design.md): same tokens, component kit
(`src/web/src/shared/ui/`), presentation mappings (`src/web/src/shared/presentation/`) and copy
rules. Nothing here adds a token or a UI library.

## 1. Visual language for remedies

The remedy is never an AI output (FR-014), so **no violet, no dashed AI border and no `AI` tag**
appear on any remedy or repair element.

| Origin | Ink (001 ui-design §1) | Example |
|--------|------------------------|---------|
| Set by policy (single remedy, suggested centre, re-evaluation result) | Grey system, mono policy code and version | "Repair · set by policy AUR-WP v2 · coveredDefect" |
| Decided by a reviewer | Blue human, solid, name and time | "Refund · decided by [REVIEWER] · 14 Oct 10:42" |
| Reported by the repair centre, recorded by staff | Blue human, with source and both times | "Marked ready · reported by phone 09:10 · recorded by [AGENT] 09:14" |

New presentation mappings in `src/web/src/shared/presentation/`:

| Value | Label | Tone |
|-------|-------|------|
| Remedy `AwaitingDecision` | Awaiting remedy decision | `warn` |
| Remedy `InProgress` | In progress | `system` |
| Remedy `Completed` | Completed | `ok` |
| Repair `AwaitingHandover` | Awaiting hand-over | `pending` |
| Repair `AtRepairCentre` | At repair centre | `system` |
| Repair `ReadyForCollection` | Ready for collection | `ok` |
| Repair `Collected` | Collected | `ok` |
| Repair `RepairNotPossible` | Repair not possible | `err` |
| Remedy types | Repair · Replacement · Refund | `system` |

## 2. Staff claim workspace — new tab `Remedy`

Route `/staff/claims/:claimId/remedy`, shown for Approved claims to every staff role. The claim
header's progress strip gains a final **Remedy** step (system when set by policy, human when a
reviewer decided), done when the remedy is `Completed`.

Layout: two columns (1.4fr / 1fr), as the AI decision tab.

**Left — Remedy card**
- Current remedy (entity title size) with the state badge and its origin line (§1).
- Policy basis: policy code and version (mono), table row, allowed remedies as badges.
- When `AwaitingDecision`: a `warn` banner with the pending reason in words ("The policy allows
  Refund or Replacement; a reviewer chooses.") and, for reviewers, the **Decide remedy** primary
  button opening the remedy dialog (§4).
- History: `Timeline` of `RemedyView.history`, newest last.

**Left — Repair card** (remedy Repair only)
- Repair status badge, active centre (name, address, opening hours, staff-only contact),
  hand-over date, centre reference (mono), failed attempts "1 of 2", estimate against the economic
  limit when the policy has one ("Estimate 310 USD · limit 288 USD").
- Action buttons rendered **only from `RepairView.allowedEvents`**: Record hand-over · Change centre
  · Record estimate · Mark ready (primary when allowed) · Repair failed · Record collection · Start
  new attempt · Transfer · Correct an entry. One primary button per view: the next step in the
  happy path.
- Each button opens one `Dialog` with the fields of that event (contracts/rest-api.delta.openapi.yaml
  `RepairEventRequest`): reported date-time (default now, not in the future), source (Phone ·
  Email · In person · Other), the event's own fields, optional note. Reason fields show
  `CharacterCount` 10–1000. Submit sends `If-Match` with the repair ETag.
- Errors: 409 shows the API message ("This repair is At repair centre; allowed: Mark ready,
  Repair failed, …"); 412 shows "This repair changed since you opened it. Reload to see the latest
  status." with a **Reload** button.

**Left — Completion card** (Replacement or Refund) with **Record completion** (date; reference,
required for Refund). *Feature 003 supersedes this card for Replacement: a Replacement remedy
shows 003's replacement card (`specs/003-replacement-fulfilment/contracts/ui-screens.md`).*

**Right — Repair events**: `Timeline` of `RepairView.events` with type, reported and recorded
times, source, actor and note; corrections are linked to the entry they correct and the corrected
entry is shown struck through, never hidden.

Auditors see every card read-only (no buttons).

## 3. Review queue

`/staff/review` lists both kinds of items. A `RemedyDecision` item shows the badge "Remedy" and the
pending reason instead of AI decision and confidence; selecting it opens the claim's Remedy tab
with the remedy dialog. Items keep the "Escalated *n* ago" ordering (oldest first).

## 4. Remedy choice in dialogs

**Review decision form** (001 §6.4), when the reviewer chooses Approve:
- A **Remedy** radio group listing only `remedyPreview.allowedRemedies`, pre-selected with
  `recommendedRemedy` when there is one, otherwise no selection and the form cannot be submitted
  until one is chosen.
- When `remedyPreviewWithProtectionPlan` is present: a **Protection plan checked** checkbox with
  the plan document link; ticking it switches the radio group to that preview's remedies.

**Remedy dialog** (Remedy tab): the same radio group from `RemedyView.allowedRemedies`, a required
reason (except the first choice of a policy choice), and the note "Only remedies the policy allows
are listed." Submit sends `If-Match` with the remedy ETag.

## 5. Claimant portal

**Claim form** (001 §6.6, step 2): a checkbox "I have an additional protection plan" that reveals a
`FileDrop` "Protection plan document · PDF, JPG or PNG". Not required to submit; when missing, the
claim later asks for it.

**Status page** (`/claims/:reference`), for Approved claims, replaces "The service centre will
contact you" with the remedy section built from `ClaimantClaimView.remedy`:
- Remedy line: "Remedy: Repair" and the label (data-model.md "Claimant projection").
- For Repair: a four-step `Timeline` (Handed over · With repair centre · Ready for collection ·
  Collected) from `steps`, and a card with the centre's name, address and opening hours. When ready,
  the card becomes an `ok` callout "Your product is ready for collection".
- For "Repair not possible" and while a remedy is being decided: a neutral `pending` banner with
  the label text only.
- Never shows notes, centre references, failure reasons or staff names (FR-041).
- *Superseded for Replacement by feature 003: the claimant label "Replacement on its way"
  (data-model.md "Claimant projection") is no longer used; replacements are collected, and the
  claimant page shows 003's replacement section and labels
  (`specs/003-replacement-fulfilment/contracts/ui-screens.md`).*

## 6. Copy

- Say "repair centre" in every new string (British spelling as in the spec); existing 001 strings
  that say "service centre" are replaced on the claimant status page only.
- Buttons state the action: "Record hand-over", "Mark ready", "Record collection".
- "With repair centre" means custody. Never write "being repaired".
