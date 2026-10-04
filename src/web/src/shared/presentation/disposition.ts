import type { Labelled } from '../ui/tone'

export type Disposition = 'AutoApprove' | 'AutoReject' | 'RequestInformation' | 'HumanReview'

const dispositions: Record<Disposition, Labelled> = {
  AutoApprove: { label: 'Auto-approved', tone: 'system' },
  AutoReject: { label: 'Auto-rejected', tone: 'system' },
  RequestInformation: { label: 'Information requested', tone: 'warn' },
  HumanReview: { label: 'Escalated to a person', tone: 'human' },
}

/** The guardrails' outcome route for an evaluation (ui-design.md §4.2). */
export function dispositionPresentation(disposition: Disposition): Labelled {
  return dispositions[disposition] ?? { label: disposition, tone: 'pending' }
}
