import type { Schemas } from '../../shared/api/client'
import type { ClaimStatus } from '../../shared/presentation'
import type { StepperStep, Tone } from '../../shared/ui'

/** How often the status page asks again while the system is still working on the claim. */
export const statusPollIntervalMs = 3_000

/** Statuses the system moves on by itself; every other status waits for a person or is final. */
export function isTransientStatus(status: ClaimStatus): boolean {
  return status === 'Submitted' || status === 'UnderEvaluation'
}

const isFinal = (status: ClaimStatus) => status === 'Approved' || status === 'Rejected'

/** The claimant stepper (ui-design.md §6.6): the status page is at step 3, or done at step 4. */
export function claimProgressSteps(status: ClaimStatus): StepperStep[] {
  const evaluation: StepperStep =
    status === 'PendingInformation'
      ? { key: 'evaluation', label: 'Information needed', state: 'blocked' }
      : status === 'UnderReview'
        ? { key: 'evaluation', label: 'Under review', state: 'current' }
        : { key: 'evaluation', label: 'Evaluation', state: isFinal(status) ? 'done' : 'current' }

  return [
    { key: 'details', label: 'Details', state: 'done' },
    { key: 'evidence', label: 'Evidence', state: 'done' },
    evaluation,
    { key: 'decision', label: 'Decision', state: isFinal(status) ? 'done' : 'upcoming' },
  ]
}

export type NextStepState = 'Done' | 'In progress' | 'Waiting for you' | 'Next'

export interface NextStep {
  key: string
  title: string
  description: string
  state: NextStepState
  tone: Tone
}

/** The "What happens next" timeline after "Received": Checked → Decision. The remedy section follows approvals. */
export function nextSteps(status: ClaimStatus): NextStep[] {
  const checked: NextStep =
    status === 'PendingInformation'
      ? {
          key: 'checked',
          title: 'Checked',
          description: 'We need a bit more information before we can finish checking your claim.',
          state: 'Waiting for you',
          tone: 'warn',
        }
      : status === 'UnderReview'
        ? {
            key: 'checked',
            title: 'Checked',
            description: 'A member of our team is checking your claim.',
            state: 'In progress',
            tone: 'human',
          }
        : isFinal(status)
          ? {
              key: 'checked',
              title: 'Checked',
              description: 'We checked your claim against your warranty.',
              state: 'Done',
              tone: 'ok',
            }
          : {
              key: 'checked',
              title: 'Checked',
              description: "We're checking your claim against your warranty.",
              state: 'In progress',
              tone: 'pending',
            }

  const decision: NextStep =
    status === 'Approved'
      ? { key: 'decision', title: 'Decision', description: 'Your claim is approved.', state: 'Done', tone: 'ok' }
      : status === 'Rejected'
        ? { key: 'decision', title: 'Decision', description: 'Your claim was not approved.', state: 'Done', tone: 'err' }
        : {
            key: 'decision',
            title: 'Decision',
            description: "We'll show the outcome here as soon as it's ready.",
            state: 'Next',
            tone: 'pending',
          }

  return [checked, decision]
}

type ClaimantRemedyView = Schemas['ClaimantRemedyView']
export type ClaimantRemedyStepState = ClaimantRemedyView['steps'][number]['state']

/** How a Repair timeline step reads to the claimant (002 contracts/ui-screens.md §5). */
export function remedyStepPresentation(state: ClaimantRemedyStepState): { state: NextStepState; tone: Tone } {
  switch (state) {
    case 'done':
      return { state: 'Done', tone: 'ok' }
    case 'current':
      return { state: 'In progress', tone: 'system' }
    default:
      return { state: 'Next', tone: 'pending' }
  }
}

/** The product is waiting at the repair centre: the centre card becomes an `ok` callout. */
export function isReadyForCollection(remedy: ClaimantRemedyView): boolean {
  return remedy.steps.some((step) => step.title === 'Ready for collection' && step.state === 'current')
}