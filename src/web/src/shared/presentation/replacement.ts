import type { Labelled } from '../ui/tone'

/**
 * Replacement values (003 contracts/ui-screens.md §1). Like a remedy, a replacement is never an AI
 * output, so no value here uses the `ai` tone; policy-derived rules are grey `system` values.
 */
export type ReplacementStatus =
  | 'AwaitingReturn'
  | 'AwaitingUnit'
  | 'ReadyForCollection'
  | 'HandedToCustomer'
  | 'NotAvailable'
  | 'Cancelled'

export type ReturnState = 'NotYetReturned' | 'Returned' | 'HeldByRepairCentre' | 'Mismatch' | 'Accepted'

export type ReplacementProductRule = 'SameModel' | 'SameCategoryAtLeastClaimValue'

export type ReplacementReturnRule = 'AtCollection' | 'BeforeReservation'

const replacementStatuses: Record<ReplacementStatus, Labelled> = {
  AwaitingReturn: { label: 'Awaiting return', tone: 'pending' },
  AwaitingUnit: { label: 'Awaiting replacement unit', tone: 'pending' },
  ReadyForCollection: { label: 'Ready for collection', tone: 'ok' },
  HandedToCustomer: { label: 'Handed to customer', tone: 'ok' },
  NotAvailable: { label: 'Replacement not available', tone: 'err' },
  Cancelled: { label: 'Cancelled — remedy changed', tone: 'system' },
}

// ui-screens.md §1 names only `Mismatch`; the other states follow the return line of §2.
const returnStates: Record<ReturnState, Labelled> = {
  NotYetReturned: { label: 'Not yet returned', tone: 'pending' },
  Returned: { label: 'Returned', tone: 'ok' },
  HeldByRepairCentre: { label: 'Held by repair centre', tone: 'system' },
  Mismatch: { label: 'Returned product does not match', tone: 'warn' },
  Accepted: { label: 'Return accepted by reviewer', tone: 'ok' },
}

const productRules: Record<ReplacementProductRule, Labelled> = {
  SameModel: { label: 'Same model only', tone: 'system' },
  SameCategoryAtLeastClaimValue: { label: 'Same model or approved equivalent', tone: 'system' },
}

const returnRules: Record<ReplacementReturnRule, Labelled> = {
  AtCollection: { label: 'Returned at collection', tone: 'system' },
  BeforeReservation: { label: 'Returned before reservation', tone: 'system' },
}

/** A `ReplacementView.pendingProposal` waiting for a reviewer. */
export const pendingModelProposalPresentation: Labelled = { label: 'Awaiting model approval', tone: 'warn' }

/** Values the contract may add later show as-is. */
export function replacementStatusPresentation(status: ReplacementStatus): Labelled {
  return replacementStatuses[status] ?? { label: status, tone: 'pending' }
}

export function returnStatePresentation(state: ReturnState): Labelled {
  return returnStates[state] ?? { label: state, tone: 'pending' }
}

export function replacementProductRulePresentation(rule: ReplacementProductRule): Labelled {
  return productRules[rule] ?? { label: rule, tone: 'system' }
}

export function replacementReturnRulePresentation(rule: ReplacementReturnRule): Labelled {
  return returnRules[rule] ?? { label: rule, tone: 'system' }
}
