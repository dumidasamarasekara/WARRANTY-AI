import type { Labelled } from '../ui/tone'

export type ClaimStatus =
  | 'Submitted'
  | 'UnderEvaluation'
  | 'PendingInformation'
  | 'UnderReview'
  | 'Approved'
  | 'Rejected'

export type FinalDecidedBy = 'System' | 'Reviewer'

const claimStatuses: Record<ClaimStatus, Labelled> = {
  Submitted: { label: 'Submitted', tone: 'pending' },
  UnderEvaluation: { label: 'Under evaluation', tone: 'pending' },
  PendingInformation: { label: 'Pending information', tone: 'warn' },
  UnderReview: { label: 'Under review', tone: 'human' },
  Approved: { label: 'Approved', tone: 'ok' },
  Rejected: { label: 'Rejected', tone: 'err' },
}

const decidedBy: Record<FinalDecidedBy, Labelled> = {
  System: { label: 'Decided by system', tone: 'system' },
  Reviewer: { label: 'Decided by reviewer', tone: 'human' },
}

/** Label and tone for a claim status (ui-design.md §4.1); the same labels for staff and claimants. */
export function claimStatusPresentation(status: ClaimStatus): Labelled {
  return claimStatuses[status] ?? { label: status, tone: 'pending' }
}

export function decidedByPresentation(value: FinalDecidedBy): Labelled {
  return decidedBy[value] ?? { label: value, tone: 'system' }
}
