import type { Labelled } from '../ui/tone'

/**
 * Remedy and repair values (002 contracts/ui-screens.md §1). A remedy is never an AI output
 * (FR-014), so no value here uses the `ai` tone.
 */
export type RemedyType = 'Repair' | 'Replacement' | 'Refund'

export type RemedyState = 'AwaitingDecision' | 'InProgress' | 'Completed'

export type RepairStatus =
  | 'AwaitingHandover'
  | 'AtRepairCentre'
  | 'ReadyForCollection'
  | 'Collected'
  | 'RepairNotPossible'

const remedyTypes: Record<RemedyType, Labelled> = {
  Repair: { label: 'Repair', tone: 'system' },
  Replacement: { label: 'Replacement', tone: 'system' },
  Refund: { label: 'Refund', tone: 'system' },
}

const remedyStates: Record<RemedyState, Labelled> = {
  AwaitingDecision: { label: 'Awaiting remedy decision', tone: 'warn' },
  InProgress: { label: 'In progress', tone: 'system' },
  Completed: { label: 'Completed', tone: 'ok' },
}

const repairStatuses: Record<RepairStatus, Labelled> = {
  AwaitingHandover: { label: 'Awaiting hand-over', tone: 'pending' },
  AtRepairCentre: { label: 'At repair centre', tone: 'system' },
  ReadyForCollection: { label: 'Ready for collection', tone: 'ok' },
  Collected: { label: 'Collected', tone: 'ok' },
  RepairNotPossible: { label: 'Repair not possible', tone: 'err' },
}

/** Repair · Replacement · Refund; values the contract may add later show as-is. */
export function remedyTypePresentation(remedy: RemedyType): Labelled {
  return remedyTypes[remedy] ?? { label: remedy, tone: 'system' }
}

export function remedyStatePresentation(state: RemedyState): Labelled {
  return remedyStates[state] ?? { label: state, tone: 'pending' }
}

export function repairStatusPresentation(status: RepairStatus): Labelled {
  return repairStatuses[status] ?? { label: status, tone: 'pending' }
}
