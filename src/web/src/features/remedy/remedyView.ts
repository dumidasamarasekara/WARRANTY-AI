import { formatDateTime, remedyTypePresentation, type RemedyType } from '../../shared/presentation'
import type { Actor } from '../../shared/ui'
import type { RemedyHistoryEntry, RemedyView } from './remedyQueries'

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
