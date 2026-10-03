# UI Design: WarrantyOS Design System for the PoC SPA

**Feature**: [spec.md](./spec.md) · **Plan**: [plan.md](./plan.md) · **Date**: 2026-10-03

**Source**: [design/WarrantyOS.dc.html](./design/WarrantyOS.dc.html) — the "WarrantyOS design system &
reference UI" clickable prototype exported from Claude Design. Open it in a browser to click through
every screen (it loads React from unpkg, so it needs internet access; `design/support.js` is the
prototype's template runtime). The source is a **reference, not code to copy**: it uses inline
styles and a template engine. The SPA re-implements it with the tokens and components below.

**Binding for**: every `src/web` task — T006, T117, T041, T070–T072, T080, T081, T089, T090, T102,
T105, T106, T112. Where the prototype and the spec, data model or REST contract disagree, the
spec and contracts win and this document records the adaptation (§2).

---

## 1. Principles

The design's visual language puts the constitution's *Explainable Decisions* (III) and
*Human-in-the-Loop* (IV) principles on screen:

1. **Three actor inks.** Violet = the AI recommends. Blue = a person decides. Grey = the system
   executes. Everything else is warm neutrals plus one green for primary actions.
2. **AI output is never styled as final.** It is labelled `AI`, tinted violet, has a **dashed**
   border, and always shows confidence as a number. Copy says *recommends, suggests, detected* —
   never *is* or *decided*.
3. **Human decisions are committed.** Blue, **solid** border, with the person's name and time. An
   override shows the AI value struck through next to the human value.
4. **System actions carry evidence of authority.** Grey, monospace IDs and timestamps, shown only
   after the guardrails passed or a person decided.
5. **Reasoning is factors, evidence and citations**, not a transcript.
6. **Overriding the AI is a primary control**, never an error state.
7. **Tenant and policy version always appear next to AI output.**
8. **Borders do the structural work; shadows are a hint.** One primary button per view.

## 2. Adaptations from the prototype to the PoC

| Prototype | PoC | Why |
|-----------|-----|-----|
| Tenants "ABC Appliances" / "XYZ Electronics", Sri Lanka, LKR | Seeded tenants "Aurora Electronics" / "Borealis Devices", regions NA/EU, the tenant's currency | spec FR-043, data-model seed (T037) |
| Roles Supervisor, Claims agent, Tenant admin, Platform admin, Service partner | `claims-agent`, `claims-reviewer`, `auditor`; one tenant per user | spec Key Entities (User), research R10 |
| Tenant switcher and "Choose a tenant" screen | **Removed.** The tenant comes from the token; the shell shows it read-only | plan: the SPA must not trust its own tenant selection; research R9 |
| Sign-in screen | Keycloak's hosted login (OIDC + PKCE) | research R10 |
| Claim statuses Auto-approved / Human review / Awaiting info | `ClaimStatus` values with spec labels (§4.1); "decided by" shown separately | FR-037 |
| Claims table with 13 columns (customer, value, risk, confidence, assignee, SLA, …) | The fields in `ClaimSummary`: reference, product, status, AI decision, disposition, submitted | contracts/rest-api.openapi.yaml |
| SLA countdowns in the review queue | "Escalated *n* ago" from `escalatedAt`; oldest first | no SLA in the spec |
| Separate "Override AI recommendation…" action | Kept as an affordance. An override is any decision that differs from the AI's (`overridesAi` is computed by the API); the dialog captures the justification | FR-034, FR-035, T087 |
| Override reason "min. 20 characters" | 10–2,000 characters | `ReviewDecisionRequest.justification` |
| Policy version applies by "claim date" | Applies by **purchase date** | research R7, contracts/rag.md |
| AI region overlays on photos | Omitted; the photo-analysis output has no coordinates | contracts/schemas/photo-analysis.schema.json |
| Customer PII masked with "reveal" flow | Customer data shown as returned by `GET /api/claims/{id}`; no reveal flow | not in the spec |
| Customer portal shows the claimant's name and a "repair time" booking | Claimants have no account; the repair request is simulated and shown only as "the service centre will contact you" | spec Assumptions, FR-037a |

**Excluded prototype screens** (no requirement or API in this feature; kept in the source for later
features): operations dashboard, AI operations, knowledge base, administration, service partner
workspace, mobile views, two-tenant demo page, notifications menu, ⌘K command palette. The design
system pages (foundations, components, AI visual language, states) are adopted as §3–§7, not as
screens.

## 3. Foundations (design tokens)

All tokens are CSS custom properties in `src/web/src/shared/styles/tokens.css`. Components use
`var(--…)`, never literal values. The PoC is **light theme only** (the design defines no dark theme).

### 3.1 Colour

| Token | Value | Use |
|-------|-------|-----|
| `--color-bg` | `#FAFAF8` | Page background |
| `--color-surface` | `#FFFFFF` | Cards, panels, inputs |
| `--color-surface-muted` | `#F2F1ED` | Context bar, wells, hover, row dividers |
| `--color-border` | `#E3E1DA` | Card borders, dividers |
| `--color-border-strong` | `#CDCAC0` | Inputs, secondary buttons |
| `--color-text` | `#1C1F1C` | Primary text |
| `--color-text-soft` | `#43473F` | Body text in secondary content |
| `--color-text-secondary` | `#5F635C` | Labels, captions |
| `--color-text-muted` | `#8B8F87` | Hints and placeholders only |
| `--color-text-disabled` | `#A2A69D` | Disabled navigation items |
| `--color-primary` | `#1F6F4A` | Primary action, focus ring, selected tab |
| `--color-primary-hover` | `#175A3B` | Primary hover, link hover |
| `--color-primary-active` | `#124A31` | Primary pressed |
| `--color-primary-soft` | `#E6F1EA` | Selected rows and list items |
| `--color-success` | `#3F7D58` | Passed, covered (dots, bars) |
| `--color-warning` | `#C08A2E` | Needs attention (dots, bars) |
| `--color-error` | `#A03428` | Failed, rejected, high risk (dots, input error border) |
| `--color-info` | `#2F5D8C` | Informational |
| `--color-ai` | `#6B4E8C` | AI fill (confidence bars, `AI` tag) |
| `--color-ai-border` | `#BFA8D6` | Dashed border of AI panels |
| `--color-human` | `#2F5D8C` | Human fill (count badges, human buttons' border) |
| `--color-system` | `#5F635C` | System fill |
| `--color-ink-inverse` | `#FFFFFF` | Text on filled backgrounds |
| `--color-chrome` | `#1C1F1C` | Toast background, brand mark |
| `--color-scrim` | `rgba(28,31,28,.4)` | Dialog backdrop |

**Tones** — every badge, alert, banner and tinted panel uses one tone triple
(`--tone-<name>-bg`, `--tone-<name>-ink`, `--tone-<name>-border`):

| Tone | bg | ink | border | Meaning |
|------|----|-----|--------|---------|
| `ok` | `#E6F1EA` | `#175A3B` | `#BFDCCB` | Approved, passed, supports |
| `warn` | `#FAF2E0` | `#7A520C` | `#E8D5A6` | Needs attention, information requested |
| `err` | `#F8E9E6` | `#8A2B20` | `#E8C2BB` | Rejected, failed, conflicts |
| `ai` | `#F0EBF5` | `#563B75` | `#D9CDE6` | AI recommendation, AI-extracted data |
| `human` | `#E8EFF6` | `#244B73` | `#C5D6E8` | Human review, human decision |
| `system` | `#F2F1ED` | `#43473F` | `#DAD7CD` | System action, neutral |
| `pending` | `#FFFFFF` | `#5F635C` | `#CDCAC0` | Waiting, not started |

**Tenant markers** — used only for the 3 px tenant strip, the tenant dot and the claimant portal
tile. Never for status:

| Tenant | Marker |
|--------|--------|
| Aurora Electronics | `#C08A2E` |
| Borealis Devices | `#1E6F7A` |
| Unknown | `#5F635C` |

### 3.2 Typography

Fonts are self-hosted through `@fontsource` packages imported in `main.tsx`, so there are no
third-party font requests and the stack works offline. Fallbacks are `Georgia, serif`,
`system-ui, sans-serif` and `ui-monospace, monospace`.

| Role | Spec | Use |
|------|------|-----|
| Display | Fraunces 600 · 40/48 | AI recommendation word in the decision workspace |
| Page title | Fraunces 600 · 32/40 | `h1` of every page |
| Section title | Fraunces 600 · 20/28 | `h2` (panel titles, dialog titles) |
| Entity title | Fraunces 600 · 24/32 | Product name in the claim header |
| Card title | Inter 600 · 16/24 | Card headings |
| Body | Inter 400 · 14/20 | Default text |
| Small | Inter 400 · 12/16, secondary colour | Captions, help text |
| Label | Inter 500 · 12/16, +0.04em, uppercase, secondary colour | Section labels, table headers, metric labels |
| Data | Inter 400 · 13/20, `tabular-nums` | Numbers in tables and metrics |
| Metric | Inter 600 · 20–24, `tabular-nums` | Confidence, risk, counts |
| Mono | JetBrains Mono 400/500 · 12/16 | Claim references, `EV-n`/`POL-n`, clause keys, model and prompt IDs, trace times |

### 3.3 Space, shape, elevation, motion

| Token set | Values |
|-----------|--------|
| Spacing `--space-1…8` | 4, 8, 12, 16, 24, 32, 48, 64 px |
| Radius | `--radius-sm` 4 (badges, inputs) · `--radius-md` 8 (buttons, cards) · `--radius-lg` 12 (dialogs) · `--radius-full` 999 (avatars, pills) |
| Elevation | `--shadow-raised` `0 1px 2px rgba(28,31,28,.05)` (cards) · `--shadow-overlay` `0 4px 12px rgba(28,31,28,.12)` (menus) · `--shadow-modal` `0 16px 40px rgba(28,31,28,.2)` (dialogs) |
| Motion | 120 ms hover · 160–200 ms enter · 240 ms fills · easing `cubic-bezier(.2,0,0,1)`; all disabled under `prefers-reduced-motion: reduce` |
| Breakpoints | sm 640 · md 1024 · lg 1280 · xl 1536 |
| Focus | `:focus-visible` → 2 px solid `--color-primary`, 2 px offset, on every interactive element |
| Layout | Staff content max-width 1440 px, padding 24/32/48 px; claimant content max-width 720 px, padding 32/24 px |

## 4. Domain-to-visual mapping

Implemented once in `src/web/src/shared/presentation/` and reused by every screen.

### 4.1 Claim status (`ClaimStatus`)

| Value | Label (staff and claimant) | Tone |
|-------|----------------------------|------|
| `Submitted` | Submitted | `pending` |
| `UnderEvaluation` | Under evaluation | `pending` |
| `PendingInformation` | Pending information | `warn` |
| `UnderReview` | Under review | `human` |
| `Approved` | Approved | `ok` |
| `Rejected` | Rejected | `err` |

When `finalDecidedBy` is present it gets its own badge: `System` → `system` tone "Decided by
system"; `Reviewer` → `human` tone "Decided by reviewer".

### 4.2 AI and guardrail values

| Field | Value → label (tone of the word) |
|-------|----------------------------------|
| `AiDecision` | `APPROVE` → Approve (`ok`) · `REJECT` → Reject (`err`) · `REQUEST_MORE_INFORMATION` → Request information (`warn`) · `HUMAN_REVIEW` → Human review (`human`) |
| `Disposition` | `AutoApprove` → Auto-approved (`system`) · `AutoReject` → Auto-rejected (`system`) · `RequestInformation` → Information requested (`warn`) · `HumanReview` → Escalated to a person (`human`) |
| `Recommendation.coverage` ("Policy match") | `COVERED` → Covered · `NOT_COVERED` → Not covered · `UNDETERMINED` → Undetermined |
| Risk `level` | `Low` → `--color-success` dot · `Medium` → `--color-warning` · `High` → `--color-error`; always with the text and, where available, the score |
| Confidence | Always the number plus a violet bar (`ConfidenceMeter`); no colour-only bands, since the minimum confidence is a per-tenant setting |
| `CheckResult` | `passed` → ✓ in `ok` · failed → ✕ in `err`, with `message` and expected vs actual |
| Evidence consistency | `match` true → "Supports" (`ok`) · false → "Conflicts" (`err`) |
| Risk signal `source` | `AI` → `AI` badge · `Deterministic` → `SYSTEM` badge |

An AI recommendation word (Approve, Reject, …) is always rendered **inside** an AI panel or next to
an `AI` badge, so its outcome colour never reads as a final decision.

### 4.3 Decision trail entries

- **Actor tone**: `actor` = `system` → System; `actor` is an agent name (`intake`, `evidence`,
  `policy`, `decision`) or the entry has `aiCalls` → AI; any other actor (a staff `sub`) → Human.
- **Dot**: the actor tone, except `AiStepFailed` → `err` and `EscalatedToReview` → `human`.
- **Step labels** (`TrailStep`): ClaimSubmitted "Claim submitted" · TenantResolved "Tenant
  resolved" · EvidenceStored "Evidence stored" · IntakeValidated "Intake validated" · ClaimExtracted
  "Claim details extracted" · CustomerVerified "Customer verified" · ProductIdentified "Product
  identified" · PolicyRetrieved "Policy retrieved" · EvidenceAnalyzed "Evidence analysed" ·
  CoverageAssessed "Coverage assessed" · RiskEvaluated "Risk evaluated" · AiRecommended "AI
  recommendation" · GuardrailsEvaluated "Guardrails evaluated" · AutoApproved "Auto-approved" ·
  AutoRejected "Auto-rejected" · InformationRequested "Information requested" · EscalatedToReview
  "Escalated to human review" · ReviewerDecided "Reviewer decision" · SupplementReceived
  "Supplement received" · ActionExecuted "Action executed" · AiStepFailed "AI step failed" ·
  Correction "Correction".

### 4.4 Formatting

- Dates `02 Oct 2026`, times 24-hour `09:41`, trace times `09:41:07` in mono; relative times
  ("escalated 2 h ago") only in queues, with the absolute time in a `title`.
- Money via `Intl.NumberFormat` with `currencyDisplay: 'code'` (`USD 1,400.00`): claim values use
  the tenant currency (`Me.tenantCurrency`); purchase prices use `purchase.currency`.
- IDs, references and clause keys in mono; never truncated without a `title`.

## 5. Component kit (`src/web/src/shared/ui/`)

One folder per component (`Component.tsx`, `Component.module.css`, `index.ts`). Plain CSS Modules
and tokens; no UI library (research R20). Built in T117 before any page.

| Component | Variants / props | States | Accessibility |
|-----------|------------------|--------|---------------|
| `Button` | `primary` · `secondary` · `ghost` · `danger` (white, error ink) · `human` (human tone, for override/escalate); sizes 36 / 40 px; `fullWidth` | default, hover, focus, active, disabled (40% opacity, `not-allowed`), loading (label + `aria-busy`) | native `<button>`; label states the action; ≥44 px touch target below 640 px |
| `Badge` | `tone` (§3.1); optional leading dot | static | text always present — never colour alone |
| `ActorBadge` | `ai` → "AI" · `human` → "HUMAN" · `system` → "SYSTEM"; optional actor name | static | text label |
| `StatusBadge` / `DecidedByBadge` | from §4.1 | static | — |
| `ConfidenceMeter` | `value` 0–100; `size` sm (56 px bar + number) / md (8 px bar under a metric) | — | `role="meter"`, `aria-valuenow/min/max`, visible number |
| `RiskIndicator` | `level`, optional `score` (bar) | — | text level always shown |
| `Card` | `padding` 16 / 20; optional header (label or card title + actions) | — | heading inside when titled |
| `AiPanel` | dashed `--color-ai-border`, header strip in `ai` tone with the `AI` tag, title and optional right-aligned meta (model · prompt version); `compact` / `full` | recommendation, invalid output, AI failure | region labelled by its header; the dashed border is backed by the "AI" label text |
| `DispositionBanner` | `human` (solid, escalated) or `ai` (dashed, automated) with tag, message and reasons | — | `role="region"` + `aria-label`; not an error alert |
| `Alert` | `ok` · `warn` · `err` · `info` (human tone) | — | `role="status"` (or `alert` for errors after a user action) |
| `Toast` | `--color-chrome` background, bottom-right | enter, dismiss; ≥6 s | `role="status"`; never the only place an action is offered |
| `Tabs` | underline: 2 px `--color-primary` on the current tab | current, hover, focus | route tabs are links with `aria-current="page"` |
| `Breadcrumbs` | tenant / section / item | — | `nav` with `aria-label="Breadcrumb"`; last item `aria-current="page"` |
| `DataTable` | header row in `--color-bg` with label typography; row dividers; clickable rows | hover, selected, loading, empty | real `<table>` semantics; row opens via a link in the first cell; horizontal scroll inside the card when narrow |
| `Pagination` | "Showing *a*–*b* of *n*", Previous / Next | disabled at ends | buttons with labels |
| `Stepper` | horizontal circles: done (filled primary + ✓), current (primary ring), blocked (warn ring), upcoming (grey) | — | ordered list; `aria-current="step"`; vertical below 640 px |
| `Timeline` | vertical rail with dots; item = dot + card | — | ordered list |
| `Dialog` | 520 px, radius 12, modal shadow, scrim | open, submitting, error | focus trap, Esc closes, focus returns to the trigger, labelled by its title |
| `Field` + `TextInput` / `TextArea` / `Select` | label above (Inter 500 13); 36 px staff, 40 px claimant; read-only variant (`--color-surface-muted`) | default, focus, error (`--color-error` border + message), disabled, read-only | visible `<label>`; hint and error via `aria-describedby`; `aria-invalid` |
| `CharacterCount` | under textareas with min/max | below min, ok, over max | announced politely |
| `FileDrop` | dashed 1.5 px `--color-border-strong`, radius 8, `--color-bg`; file rows with name, size, status | drag-over, uploading, error, done | real file `<input>` behind the drop zone; keyboard reachable |
| `KeyValueList` | 110 px label column, values; mono option | — | `<dl>` |
| `FactorRow` | 22 px circle with ✓ / ! / ✕ in the tone's ink + text | — | the symbol has a text alternative |
| `PolicyCitation` | `ai`-tone box: document title · clause key · clause title; version; effective from–to; excerpt; chips "Effective *date*" and "*Tenant* only" | cited / not cited | — |
| `EvidenceTile` | hatched placeholder (`repeating-linear-gradient` of `--color-surface-muted`/`--color-bg`) until the authorized image loads; name + kind | loading, loaded, PDF, error | image `alt` = file name and kind |
| `EmptyState`, `LoadingState`, `ProblemState` | message, optional action; `ProblemState` renders RFC 9457 `title`/`detail` and the correlation ID in mono | — | live region for loading |

## 6. Screens

### 6.1 Staff shell (T041, T080, T106)

- **Sidebar**: 240 px; brand mark (28 px `--color-chrome` tile with "W" in Fraunces) + "WarrantyOS";
  items with a 2-letter tile and label: **Claims** (CL, all staff), **Review queue** (HR,
  `claims-reviewer` only, with a count badge from `GET /api/review-queue`), **Policies** (WP, all
  staff). The current item uses `--color-primary-soft` with a primary tile. Collapses to a 64 px
  rail below 1280 px or via the "Collapse" button at the bottom.
- **Tenant strip**: 3 px bar in the tenant marker colour at the top of the content column.
- **Header** (56 px, white): breadcrumbs left; right: read-only tenant chip (marker dot + tenant
  display name, no menu) and the user button (initials avatar + name) opening a menu with name,
  roles and **Sign out**.
- **Context bar** (`TenantBanner`, 32 px, `--color-surface-muted`): "Tenant **Aurora
  Electronics**" · "Environment **Local PoC**" · right-aligned "Data isolated to this tenant".
- **Landing**: `/staff` redirects to the review queue for reviewers, else the claims list.
- **Auditors** (T106): same shell, Claims and Policies only; review actions are never rendered.

### 6.2 Claims list — `/staff/claims` (T089, T106)

- Page title "Claims", subtitle "*n* claims · *tenant*". Actions: **New claim** (primary,
  `claims-agent` only).
- Filter: status select (the only filter `GET /api/claims` supports).
- `DataTable` columns: Reference (mono, link) · Product · Status (`StatusBadge`) · AI decision
  (`ActorBadge ai` + label) · Outcome route (disposition badge) · Submitted (date). Reviewers and
  auditors also get a **Trace** link column.
- `Pagination` from `page`, `pageSize`, `total`.

### 6.3 Claim workspace — `/staff/claims/:claimId/(case|decision|evidence|trace)` (T089, T102, T105)

**Claim header card** (all tabs): reference (mono) over the product name (entity title); status and
decided-by badges; key facts on the right — Claim value (tenant currency), Customer, Region, Risk
(badge "Medium · 57"). Below it, the **progress strip**: Submitted (system) → Intake (AI) →
Evidence (AI) → Policy (AI) → Risk (system) → Decision (AI) → Guardrails (system) → Outcome
(system for auto-finalized or information requested, human for escalated) → Review (human, only
when escalated). A step is done when its part of `latestEvaluation` is present; the current
step gets a 2 px ring; future steps are `pending` with a dashed border. Then the tabs: **Case
file** · **AI decision** · **Evidence** · **Decision trace** (trace for reviewers and auditors only).

**Case file** — two columns (1fr / 380 px sticky):
- Left: *Customer* (name, email, country) · *Product & purchase* (product name, model code and
  serial in mono, category, "In catalog" yes/no, purchase date, place, price) · *Claim* (claim date,
  region, channel, the problem description in quotes captioned "Customer-written text · original
  evidence") · *Uploaded evidence* (4-column `EvidenceTile` grid, "Open viewer →").
- Right: compact `AiPanel` "Recommendation · not final" (or "Recommendation · finalized by
  system/reviewer") with the decision word, `ConfidenceMeter`, `RiskIndicator`, policy match and the
  first cited clause, then the top factor rows; a `DispositionBanner` at its foot; button "Open AI
  decision".
- `claims-agent` on a `PendingInformation` claim: **Add supplement** (secondary) opening the
  supplement dialog (T102).

**AI decision** — `DispositionBanner` on top (disposition + `guardrails.reasons`), then two columns
(1.4fr / 1fr):
- Left: full `AiPanel` "AI decision" (meta: `recommendation.model` · `promptVersion`) with four
  metrics — Recommendation (display type), Confidence, Risk, Policy match. *Why — decision factors*:
  `reasoningSummary` then one `FactorRow` per guardrail check (expected vs actual on failure).
  *Evidence references*: one row per `evidenceRefs` item — `EV-n` chip (opens the Evidence tab on
  that item), observation, and the consistency tag when one exists.
- Right: *Policy evidence* — `PolicyCitation` per `policyReferences` item (cited first), the Policy
  agent's confidence, link "View policy versions →". *Action* — for automated outcomes a `SYSTEM`
  badge with "Executed after guardrails passed · *time*"; for escalations "Escalated — a reviewer
  must decide" and, for reviewers, **Open in review** (`human` button). *Risk signals* — code,
  source badge, severity, detail. *Model information* — model, prompt version.
- `isValid = false` → `err` alert listing `validationErrors`; `failureReason` → `warn` alert "AI
  analysis could not be completed — routed to human review" (FR-031).

**Evidence** — three columns (260 px / 1fr / 340 px):
- Left: evidence list (file name, kind, round, `EV-n`); selected item in `--color-primary-soft`.
- Centre: viewer with a `SYSTEM` "Original upload" header; images and PDFs fetched from
  `GET /api/claims/{id}/evidence/{evidenceId}/content` with the bearer token and shown from an
  object URL (`<img>` or `<object type="application/pdf">`).
- Right: *AI-extracted* card (`ai` badge, caption "Verify before relying"): invoice fields and the
  consistency rows (field · claim value · evidence value · Supports/Conflicts). *AI interpretation*
  card (dashed): photo observations with the Evidence agent's confidence.

**Decision trace** (T105) — title "Decision trace", subtitle "*reference* · *n* entries ·
append-only"; integrity badge (`ok` "Hash chain verified" or `err` "Integrity check failed" plus an
alert); toggle **View: Operational / Technical**. `Timeline` of entries; each card is a 3-column
grid (200 px / 1fr / 110 px): "*seq*. *step label*" with the `ActorBadge` and actor name · summary ·
time. The technical view expands AI calls (agent, model, prompt version, input/output/cache tokens,
latency, cost, status, attempt), tool calls (tool, allowed, latency, summary) and RAG queries
(namespaces, result clause keys, latency) in a mono well; `attempt > 1` shows "↻ retried" in the
`warn` ink.

### 6.4 Review queue — `/staff/review/:claimId?` (T090)

- Page title "Human review", subtitle "Claims the AI could not safely decide on its own ·
  *tenant*".
- Left (300 px): queue cards oldest first — reference (mono, human ink), product, claim value ·
  escalation reasons, "Escalated *n* ago"; selected card in the `human` tone. `EmptyState` "No
  claims waiting for review".
- Right workspace:
  - `DispositionBanner` (human): "Human review required", the reasons, and "You are the control
    point — the AI recommendation below is advisory."
  - Two cards side by side: *AI recommendation* (dashed `AiPanel`: decision word, confidence, cited
    policy) and *Your decision* (`human` tone, solid): "Pending" until decided, then the human
    value next to the struck-through AI value when overriding.
  - *Risk signals & evidence* and *Decision history* (AI recommended → SYSTEM guardrails escalated
    with reasons → HUMAN decisions with reviewer and time). Link "Open full case file" to the claim
    workspace for the complete FR-033 view.
  - Action bar: **Approve** (primary) · **Reject** (danger) · **Request more information**
    (secondary) · right-aligned **Override AI recommendation…** (`human`).
- Decision rules: approving in agreement with the AI submits directly; any decision that differs
  from the AI's, and any rejection, opens the justification `Dialog` ("Override AI
  recommendation" / "Reject claim": "AI recommends **X**. You are choosing **Y**. This is recorded
  as a human decision with your reason, linked to the decision trace."; textarea with
  `CharacterCount` 10–2,000; confirm disabled until valid). Request information opens the same
  dialog with a requested-items picker. Every submission sends `If-Match`; 409 → "This claim has
  already been decided" with a refresh; 412 → "The claim changed since you opened it — reload".
  Success → toast "Decision recorded" and the claim leaves the queue.

### 6.5 Policies — `/staff/policies` (T081)

- Page title "Warranty policies — *tenant*", subtitle "The AI applies the version in force on the
  purchase date, never the latest by default."
- Left (260 px): versions grouped by `policyCode`; each "Policy v*n*", effective dates and a status
  badge computed from today — Active (`ok`), Superseded (`system`), Scheduled (`pending`).
- Right: *Applicability timeline* (segments per version proportional to their date ranges; active
  version in `--color-primary-soft` with a primary left border, open-ended versions to "today +
  6 months", scheduled versions dashed) and a details card (title, regions, effective from–to). An
  `ai`-tone note: "Retrieval filters on purchase date. A product bought on *date in an older
  version* is assessed under that version even if reviewed today."

### 6.6 Claimant portal — tenant channel host (T070, T071, T102)

- **Branded header** (56 px, white): 28 px tile in the tenant marker colour with the display name's
  initials (Fraunces), display name from `GET /api/public/tenant`, "Warranty claims". No account
  name.
- **Stepper** (four steps): Details → Evidence → Evaluation → Decision. The submit form uses steps
  1–2; the status page sets step 3 for Submitted, UnderEvaluation ("Evaluation"), UnderReview
  ("Under review") and PendingInformation ("Information needed", blocked state) and completes step
  4 for Approved/Rejected.
- **Submit** (`/`): step 1 *Your details* (customer, product model code and serial, purchase date,
  place, price, currency, country); step 2 *What went wrong and your files* (description with
  `CharacterCount` 20–4,000, `FileDrop` for one invoice and 1–8 photos: "PDF, JPG, PNG or WebP · up
  to 15 MB each"; HEIC → "Please send photos as JPEG or PNG"). Back / **Submit claim**. Success: an
  `ok` card "Claim submitted", the reference in large mono, "Use this reference and the email
  address or phone number you gave to check your claim", button **Check status**.
- **Access** (`/claims/access`): reference + email or phone, **View claim**. Any 401 → "We couldn't
  find a claim with those details" (never reveals existence); 429 → "Too many attempts — try again
  in *n* minutes".
- **Status** (`/claims/:reference`): outcome banner — Approved `ok` "Your claim is approved" +
  product + `outcomeExplanation`; Rejected `err` "Your claim was not approved" + explanation;
  PendingInformation `warn` "We need a bit more information" + requested items + `SupplementForm`;
  otherwise `pending` "We're checking your claim" (polling, announced in a polite live region).
  *What happens next* timeline: Received (`submittedAt`) → Checked → Decision → for approvals
  "The service centre will contact you". Never shows risk, fraud or internal reasoning (FR-037).
- **Staff new claim** (`/staff/claims/new`, T072): the same `ClaimForm` in the staff shell, titled
  "New claim" with "Submitted on behalf of a customer · Agent portal".

## 7. Responsive and accessibility rules

- **≥1280 px**: 240 px sidebar; two- and three-column workspaces.
- **768–1279 px**: sidebar becomes a 64 px rail; claim and review workspaces stack the right column
  under the left; metric and card grids drop to two columns below 1000 px and one below 768 px;
  tables scroll horizontally inside their card.
- **<768 px**: the claimant portal is fully supported (single column, full-width buttons, ≥44 px
  targets, vertical stepper). The staff workspace is not a PoC target below 768 px beyond remaining
  usable with horizontal scrolling.
- Text contrast ≥4.5:1 on every tone surface (e.g., AI ink `#563B75` on `#F0EBF5` is 8.1:1). Status
  is never conveyed by colour alone. Keyboard: tab order follows reading order, Esc closes overlays,
  dialogs trap and return focus. Reduced motion respected.

## 8. Copy rules

- AI copy uses *recommends / suggests / detected*; final outcomes name the decider ("Auto-approved
  by the system after guardrails passed", "Decided by *reviewer* on *date*").
- Tenant name and policy version appear next to every AI recommendation and policy citation.
- Claimant copy is plain language: no check codes, agent names, risk or confidence.
- Errors from the API render `ProblemDetails.title` and `detail` plus the correlation ID for
  support; validation errors attach to their fields.

## 9. Traceability

| Prototype screen | PoC route | Task(s) | Status |
|------------------|-----------|---------|--------|
| Foundations, Components, AI visual language, States · A11y · Responsive | `shared/styles`, `shared/ui`, `shared/presentation` | T006, T117 | Adopted |
| Operations console shell | staff layout | T041, T080, T106 | Adapted (§2, §6.1) |
| Claims | `/staff/claims` | T089, T106 | Adapted (contract columns) |
| Claim detail · AI decision · Evidence · Decision trace | `/staff/claims/:claimId/*` | T089, T102, T105 | Adapted (no overlays, no PII reveal) |
| Human review | `/staff/review/:claimId?` | T090 | Adapted (FR-035 rules, no SLA) |
| Policy versions | `/staff/policies` | T081 | Adapted (purchase date, no clause list) |
| Customer · Submit claim / Track claim | claimant `/`, `/claims/access`, `/claims/:reference` | T070, T071, T102 | Adapted (no account, simulated repair) |
| Sign in, Tenant selection | Keycloak login | T007, T041 | Replaced |
| Dashboard, AI operations, Knowledge base, Administration, Service partner, Mobile, Demo | — | — | Out of scope (§2) |
