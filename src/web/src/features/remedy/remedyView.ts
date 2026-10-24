import { formatDateTime, remedyTypePresentation, type RemedyType } from '../../shared/presentation'
import type { Actor } from '../../shared/ui'
import type { RemedyHistoryEntry, RemedyView, RepairEvent, RepairEventType, RepairView, ReportSource } from './remedyQueries'

/** Limits of `RemedyDecisionRequest.reason` (002 rest-api delta). */
export const remedyReasonLimits = { min: 10, max: 1000 } as const

const scenarioLabels: Record<RemedyView['scenario'], string> = {
  CoveredDefect: 'Covered defect',
  ProtectionPlanDamage: 'Protection plan damage',
  RepairNotEconomical: 'Repair not economical',
  RepairFailed: 'Repair failed',
}

export const scenarioLabel = (scenario: RemedyView['scenario']) => scenarioLabels[scenario] ?? scenario

export const remedyLabel = (remedy: RemedyType) => remedyTypePresentation(remedy).label

/** "Refund", "Refund or Replacement", "Repair, Replacement or Refund". */
export function remedyList(remedies: readonly RemedyType[]): string {
  const labels = remedies.map(remedyLabel)
  if (labels.length <= 1) return labels.join('')
  return `${labels.slice(0, -1).join(', ')} or ${labels[labels.length - 1]}`
}

const pendingCauses: Record<NonNullable<RemedyView['pendingReason']>, string> = {
  POLICY_CHOICE: '',
  NO_REMEDY_RULE: 'The policy has no remedy rule for this case. ',
  REPAIR_FAILED_LIMIT: 'The repair failed as many times as the policy allows. ',
  REPAIR_NOT_ECONOMICAL: 'The repair estimate is above the policy’s economic limit. ',
}

/** The pending reason in words (ui-screens.md §2): "The policy allows Refund or Replacement; a reviewer chooses." */
export function pendingReasonText(remedy: Pick<RemedyView, 'pendingReason' | 'allowedRemedies'>): string {
  const cause = remedy.pendingReason ? (pendingCauses[remedy.pendingReason] ?? '') : ''
  const choice =
    remedy.allowedRemedies.length > 0 ? `The policy allows ${remedyList(remedy.allowedRemedies)}; a reviewer chooses.` : 'A reviewer chooses the remedy.'
  return `${cause}${choice}`
}

export interface RemedyOrigin {
  actor: Exclude<Actor, 'ai'>
  /** "set by policy AUR-WP v2 · coveredDefect" or "decided by [REVIEWER] · 14 Oct 10:42". */
  text: string
}

/** The history entry that set the current remedy: the latest one carrying a remedy. */
function settingEntry(remedy: RemedyView): RemedyHistoryEntry | undefined {
  return [...remedy.history].reverse().find((entry) => entry.remedy !== undefined)
}

/**
 * Who set the current remedy (ui-screens.md §1): grey system with the policy code and version when the
 * policy set it, blue human with the reviewer's name and time when a reviewer decided. A remedy is never
 * an AI output, so there is no AI origin.
 */
export function remedyOrigin(remedy: RemedyView): RemedyOrigin | undefined {
  if (!remedy.remedy) return undefined
  const entry = settingEntry(remedy)
  if (entry?.decidedBy === 'Reviewer') {
    return { actor: 'human', text: `decided by ${entry.actor} · ${formatDateTime(entry.createdAt)}` }
  }
  return { actor: 'system', text: `set by policy ${policyBasis(remedy)}` }
}

/** "AUR-WP v2 · coveredDefect". */
export const policyBasis = (remedy: Pick<RemedyView, 'policyVersion' | 'tableRow'>) =>
  `${remedy.policyVersion.policyCode} v${remedy.policyVersion.version} · ${remedy.tableRow}`

/** A reason is required for every decision except the first decision of a policy choice (FR-013). */
export function reasonRequired(remedy: RemedyView): boolean {
  const firstChoice =
    remedy.state === 'AwaitingDecision' && remedy.pendingReason === 'POLICY_CHOICE' && !remedy.history.some((entry) => entry.kind === 'Decided')
  return !firstChoice
}

/**
 * The claim header's Remedy step actor: system when the policy set the remedy, human when a reviewer
 * decided it or has to.
 */
export const remedyStepActor = (remedy: RemedyView): Exclude<Actor, 'ai'> =>
  remedy.state === 'AwaitingDecision' ? 'human' : (remedyOrigin(remedy)?.actor ?? 'system')

/** Search parameter that opens the remedy dialog on the Remedy tab, e.g. from a review-queue remedy item. */
export const decideRemedyParam = 'decide'

/** Limits of `RepairEventRequest.reason` (002 rest-api delta). */
export const repairReasonLimits = { min: 10, max: 1000 } as const

const repairEventLabels: Record<string, string> = {
  CentreChanged: 'Centre changed',
  HandedOver: 'Handed over',
  EstimateReported: 'Estimate recorded',
  MarkedReady: 'Marked ready',
  Collected: 'Collected',
  RepairFailed: 'Repair failed',
  NewAttemptStarted: 'New attempt started',
  Transferred: 'Transferred',
  Correction: 'Correction',
}

/** Past-tense label of a recorded repair event; types the contract may add later show as-is. */
export const repairEventLabel = (type: string) => repairEventLabels[type] ?? type

const reportSourceLabels: Record<ReportSource, string> = { Phone: 'Phone', Email: 'Email', InPerson: 'In person', Other: 'Other' }

export const reportSources = Object.keys(reportSourceLabels) as ReportSource[]

export const reportSourceLabel = (source: string) => reportSourceLabels[source as ReportSource] ?? source

/** "Marked ready · reported by phone 09:10 · recorded by [AGENT] 09:14" (ui-screens.md §1). */
export function repairEventOrigin(event: Pick<RepairEvent, 'reportedAt' | 'recordedAt' | 'source' | 'actor'>): string {
  return `reported by ${reportSourceLabel(event.source).toLowerCase()} ${formatDateTime(event.reportedAt)} · recorded by ${event.actor} ${formatDateTime(event.recordedAt)}`
}

/** Event types this dialog records; T250 and T257 add the remaining ones. */
export const repairDialogEvents = ['HandedOver', 'CentreChanged', 'MarkedReady', 'Collected'] as const satisfies readonly RepairEventType[]

export type RepairDialogEvent = (typeof repairDialogEvents)[number]

export const isRepairDialogEvent = (type: RepairEventType): type is RepairDialogEvent =>
  (repairDialogEvents as readonly RepairEventType[]).includes(type)

/** Button and dialog title per event: the action, as ui-screens.md §6. */
export const repairActionLabels: Record<RepairDialogEvent, string> = {
  HandedOver: 'Record hand-over',
  CentreChanged: 'Change centre',
  MarkedReady: 'Mark ready',
  Collected: 'Record collection',
}

/** Happy-path order: the first allowed of these is the one primary button. */
const happyPath: readonly RepairEventType[] = ['HandedOver', 'MarkedReady', 'Collected']

/** Action buttons in the contract's order, rendered only from `RepairView.allowedEvents`. */
export function repairActions(repair: Pick<RepairView, 'allowedEvents'>): { type: RepairDialogEvent; primary: boolean }[] {
  const allowed = repair.allowedEvents.filter(isRepairDialogEvent)
  const primary = happyPath.find((type) => allowed.includes(type as RepairDialogEvent))
  const order = Object.keys(repairActionLabels) as RepairDialogEvent[]
  return order.filter((type) => allowed.includes(type)).map((type) => ({ type, primary: type === primary }))
}

/** Remedies completed by a recorded completion; a Repair completes through its Collected event. */
export type CompletableRemedy = 'Replacement' | 'Refund'

export const isCompletableRemedy = (remedy: RemedyType | undefined): remedy is CompletableRemedy =>
  remedy === 'Replacement' || remedy === 'Refund'

/** Copy of the completion card per remedy (FR-034, FR-035). */
export const completionCopy: Record<CompletableRemedy, { done: string; pending: string; hint: string; dateLabel: string }> = {
  Replacement: {
    done: 'Replacement handed to customer',
    pending: 'The replacement has not been handed to the customer yet.',
    hint: 'Record the date the replacement was handed to the customer.',
    dateLabel: 'Handed to customer on',
  },
  Refund: {
    done: 'Refund completed',
    pending: 'The refund has not been completed yet.',
    hint: 'Record the date and reference of the refund completed through the refund option.',
    dateLabel: 'Refund completed on',
  },
}
