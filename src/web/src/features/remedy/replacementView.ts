import { formatDateTime, replacementProductRulePresentation, replacementReturnRulePresentation } from '../../shared/presentation'
import type { RemedyView, ReplacementEvent, ReplacementEventType, ReplacementView } from './remedyQueries'
import { reportSourceLabel } from './remedyView'

/** Limits of `ReplacementEventRequest.reason` (003 rest-api delta). */
export const replacementReasonLimits = { min: 10, max: 1000 } as const

const replacementEventLabels: Record<string, string> = {
  LocationSuggested: 'Location suggested',
  LocationChanged: 'Location changed',
  ReturnRecorded: 'Return recorded',
  ReturnAccepted: 'Return accepted',
  ModelProposed: 'Model proposed',
  ModelApproved: 'Model approved',
  ModelDeclined: 'Model declined',
  UnitReserved: 'Unit reserved',
  UnitRefused: 'Customer refused unit',
  Moved: 'Moved',
  HandedToCustomer: 'Handed to customer',
  NotAvailable: 'Not available',
  Cancelled: 'Cancelled',
  Correction: 'Correction',
}

/** Past-tense label of a recorded replacement event; types the contract may add later show as-is. */
export const replacementEventLabel = (type: string) => replacementEventLabels[type] ?? type

/** The actor of entries the system records (the suggested location, a cancellation). */
export const systemActor = 'system'

export const isSystemEvent = (event: Pick<ReplacementEvent, 'actor' | 'source'>) => event.actor === systemActor || event.source === 'System'

/**
 * "Unit reserved · reported by phone 09:10 · recorded by [AGENT] 09:14" (ui-screens.md §1); a system entry only has
 * the time it was recorded.
 */
export function replacementEventOrigin(event: Pick<ReplacementEvent, 'reportedAt' | 'recordedAt' | 'source' | 'actor'>): string {
  if (isSystemEvent(event)) return `recorded by the system ${formatDateTime(event.recordedAt)}`
  return `reported by ${reportSourceLabel(event.source).toLowerCase()} ${formatDateTime(event.reportedAt)} · recorded by ${event.actor} ${formatDateTime(event.recordedAt)}`
}

/** Anchor id of a replacement event entry, so a correction can link to the entry it corrects. */
export const replacementEventAnchor = (seq: number) => `replacement-event-${seq}`

/** "Entry 3 · Unit reserved": how a correction names the entry it corrects. */
export const replacementEventName = (event: Pick<ReplacementEvent, 'seq' | 'type'>) => `Entry ${event.seq} · ${replacementEventLabel(event.type)}`

/**
 * Event types the replacement event dialog records, in the button order of ui-screens.md §2. Allowed events without a
 * dialog yet (refusals, moves and corrections come with US5) render no button.
 */
export const replacementDialogEvents = [
  'LocationChanged',
  'ReturnRecorded',
  'ModelProposed',
  'ModelApproved',
  'ModelDeclined',
  'ReturnAccepted',
  'UnitReserved',
  'HandedToCustomer',
  'NotAvailable',
] as const satisfies readonly ReplacementEventType[]

export type ReplacementDialogEvent = (typeof replacementDialogEvents)[number]

export const isReplacementDialogEvent = (type: ReplacementEventType): type is ReplacementDialogEvent =>
  (replacementDialogEvents as readonly ReplacementEventType[]).includes(type)

/** Button and dialog title per event: the action (ui-screens.md §2, §5). */
export const replacementActionLabels: Record<ReplacementDialogEvent, string> = {
  LocationChanged: 'Change location',
  ReturnRecorded: 'Record return',
  ModelProposed: 'Propose other model',
  ModelApproved: 'Approve model',
  ModelDeclined: 'Decline model',
  ReturnAccepted: 'Accept return',
  UnitReserved: 'Reserve unit',
  HandedToCustomer: 'Record hand-over to customer',
  NotAvailable: 'Not available',
}

/**
 * Happy-path order: the first allowed of these is the one primary button. A pending model proposal is decided first
 * (only reviewers may), then reserving and handing over; the return is the next step only while nothing else can move
 * the replacement on (`AwaitingReturn`, or a mismatch a reviewer must accept). Proposing another model and "not
 * available" are never primary.
 */
const happyPath: readonly ReplacementEventType[] = ['ModelApproved', 'UnitReserved', 'HandedToCustomer', 'ReturnRecorded', 'ReturnAccepted']

/** Action buttons in the contract's order, rendered only from `ReplacementView.allowedEvents`. */
export function replacementActions(
  replacement: Pick<ReplacementView, 'allowedEvents'>,
): { type: ReplacementDialogEvent; primary: boolean }[] {
  const allowed = replacement.allowedEvents.filter(isReplacementDialogEvent)
  const primary = happyPath.find((type) => allowed.includes(type as ReplacementDialogEvent))
  return replacementDialogEvents.filter((type) => allowed.includes(type)).map((type) => ({ type, primary: type === primary }))
}

/** "AUR-WP v2 · Same model or approved equivalent · Returned at collection" (ui-screens.md §2). */
export const replacementPolicyBasis = (
  remedy: Pick<RemedyView, 'policyVersion'>,
  replacement: Pick<ReplacementView, 'productRule' | 'returnRule'>,
) =>
  `${remedy.policyVersion.policyCode} v${remedy.policyVersion.version} · ${replacementProductRulePresentation(replacement.productRule).label} · ${replacementReturnRulePresentation(replacement.returnRule).label}`

/**
 * Under `AtCollection` the hand-over confirms the faulty product came back, unless it was already returned, is held by
 * a repair centre or a reviewer accepted the return (data-model.md, `HandedToCustomer`).
 */
export const needsReturnConfirmation = (replacement: Pick<ReplacementView, 'returnRule' | 'returnState'>) =>
  replacement.returnRule === 'AtCollection' && !['Returned', 'HeldByRepairCentre', 'Accepted'].includes(replacement.returnState)

/** "Aurora Tab 11 (AUR-TAB11)", or the model code alone when the name is unknown. */
export const replacementModelText = (model: { modelCode: string; name?: string }) =>
  model.name ? `${model.name} (${model.modelCode})` : model.modelCode

/** Search parameter of the Remedy tab that focuses the replacement card (a `ReplacementCheck` from the review queue). */
export const focusReplacementParam = 'replacement'
