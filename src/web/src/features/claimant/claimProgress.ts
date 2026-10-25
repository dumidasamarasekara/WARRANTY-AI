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

/**
 * The claimant label of a remedy (002 and 003 data-model.md "Claimant projection"): a Refund reads from `completed`;
 * every other remedy shows the label the API computed. A Replacement's label follows its collection ("We are preparing
 * your replacement", "Your replacement is ready for collection"); it is never "on its way" (003 ui-screens.md §5).
 */
export function claimantRemedyLabel(remedy: ClaimantRemedyView): string {
  if (remedy.remedy === 'Refund') return remedy.completed ? 'Refund completed' : 'Refund being processed'
  return remedy.label
}

/** A Replacement or Refund that is done: the label becomes an `ok` callout. */
export const isFulfilmentCompleted = (remedy: ClaimantRemedyView) =>
  (remedy.remedy === 'Replacement' || remedy.remedy === 'Refund') && remedy.completed === true

/** The API's claimant label of a replacement waiting at its location (003 data-model.md "Claimant projection"). */
export const replacementReadyLabel = 'Your replacement is ready for collection'

const replacementStep = (remedy: ClaimantRemedyView, title: string) => remedy.replacementSteps?.find((step) => step.title === title)

/** The policy wants the faulty product before a unit is reserved, and it has not been returned yet. */
export const isAwaitingReturn = (remedy: ClaimantRemedyView) =>
  remedy.remedy === 'Replacement' && replacementStep(remedy, 'Product returned')?.state === 'current'

/**
 * A unit waits for the customer at the location: the location card becomes an `ok` callout with the model name. The
 * timeline marks "Ready for collection" current from the moment a unit is being prepared, so readiness reads from the
 * label and the model name, which the API only sends once a unit is reserved.
 */
export const isReplacementReady = (remedy: ClaimantRemedyView) =>
  remedy.remedy === 'Replacement' &&
  remedy.completed !== true &&
  (remedy.label === replacementReadyLabel || remedy.replacementModelName !== undefined)

/**
 * The faulty product comes back when the replacement is collected (return rule `AtCollection`): the timeline has a
 * "Product returned" step only when the product had to be returned first or a repair centre holds it.
 */
export const bringsFaultyProductAtCollection = (remedy: ClaimantRemedyView) =>
  remedy.remedy === 'Replacement' && replacementStep(remedy, 'Product returned') === undefined
