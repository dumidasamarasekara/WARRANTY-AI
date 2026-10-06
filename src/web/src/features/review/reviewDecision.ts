import type { Schemas } from '../../shared/api/client'
import { aiDecisionPresentation } from '../../shared/presentation'

export type ClaimDetail = Schemas['ClaimDetail']
export type ReviewQueueItem = Schemas['ReviewQueueItem']
export type ReviewDecisionKind = Schemas['ReviewDecisionRequest']['decision']
export type RequestedItem = Schemas['RequestedItem']

/** The AI decisions a reviewer can agree with or override (FR-035). */
export type DecidingAiDecision = 'APPROVE' | 'REJECT'

/** Message to the claimant (Approve/Reject) and internal justification limits (FR-035, FR-036). */
export const claimantMessageLimits = { min: 20, max: 1500 } as const
export const justificationLimits = { min: 10, max: 2000 } as const

const claimantTextSafeCheck = 'CLAIMANT_TEXT_SAFE'

const humanDecisionFor: Record<DecidingAiDecision, ReviewDecisionKind> = { APPROVE: 'Approve', REJECT: 'Reject' }

const decisionLabels: Record<ReviewDecisionKind, string> = {
  Approve: 'Approve',
  Reject: 'Reject',
  RequestInformation: 'Request information',
}

export function reviewDecisionLabel(decision: ReviewDecisionKind): string {
  return decisionLabels[decision] ?? decision
}

/**
 * The AI decision a reviewer agrees with or overrides: a valid `APPROVE` or `REJECT` only. A missing
 * or invalid recommendation, `HUMAN_REVIEW` and `REQUEST_MORE_INFORMATION` are "No AI decision" and
 * no reviewer decision counts as an override (ui-design.md §6.4, FR-035).
 */
export function decidingAiDecision(claim: ClaimDetail): DecidingAiDecision | null {
  const recommendation = claim.latestEvaluation?.recommendation
  if (!recommendation?.isValid) return null
  return recommendation.decision === 'APPROVE' || recommendation.decision === 'REJECT' ? recommendation.decision : null
}

/** The AI decision as the reviewer's equivalent (`APPROVE` → Approve), for the struck-through comparison. */
export function aiDecisionAsReview(decision: DecidingAiDecision): ReviewDecisionKind {
  return humanDecisionFor[decision]
}

/** Readable label of a deciding AI decision ("Approve", "Reject"). */
export function aiDecisionLabel(decision: DecidingAiDecision): string {
  return aiDecisionPresentation(decision).label
}

/** True when the reviewer's decision differs from a valid `APPROVE`/`REJECT` recommendation. */
export function overridesAi(claim: ClaimDetail, decision: ReviewDecisionKind): boolean {
  const ai = decidingAiDecision(claim)
  return ai !== null && humanDecisionFor[ai] !== decision
}

/** The internal justification is required when overriding the AI and for every rejection. */
export function requiresJustification(claim: ClaimDetail, decision: ReviewDecisionKind): boolean {
  return decision === 'Reject' || overridesAi(claim, decision)
}

/** Approve and Reject always carry a message to the claimant; information requests never do. */
export function requiresClaimantMessage(decision: ReviewDecisionKind): boolean {
  return decision !== 'RequestInformation'
}

/**
 * Pre-fill for the message to the claimant: the AI's `claimantExplanation`, only when the decision
 * matches a valid AI recommendation and that text passed the `CLAIMANT_TEXT_SAFE` guardrail check.
 * Empty otherwise, so the reviewer writes the message themselves.
 */
export function claimantMessagePrefill(claim: ClaimDetail, decision: ReviewDecisionKind): string {
  const ai = decidingAiDecision(claim)
  if (ai === null || humanDecisionFor[ai] !== decision) return ''
  const explanation = claim.latestEvaluation?.recommendation?.claimantExplanation?.trim()
  if (!explanation) return ''
  const safe = claim.latestEvaluation?.guardrails?.checks?.some((check) => check.code === claimantTextSafeCheck && check.passed)
  return safe ? explanation : ''
}

/** Character length within limits, counting the trimmed text as the API does. */
export function withinLimits(text: string, limits: { min: number; max: number }): boolean {
  const length = text.trim().length
  return length >= limits.min && length <= limits.max
}

export type RequestedItemCode =
  | 'INVOICE'
  | 'LEGIBLE_INVOICE'
  | 'PHOTO_OF_DAMAGE'
  | 'PHOTO_OF_SERIAL_LABEL'
  | 'PURCHASE_DATE'
  | 'PROBLEM_DETAILS'
  | 'OTHER'

export interface RequestedItemOption {
  item: RequestedItemCode
  label: string
  /** Claimant-facing reason sent with the item; `OTHER` takes the reviewer's own wording. */
  reason: string
}

/** The requested-item codes of the decision schema, with the claimant-facing reason sent for each. */
export const requestedItemOptions: readonly RequestedItemOption[] = [
  { item: 'INVOICE', label: 'Invoice or receipt', reason: 'Please upload the invoice or receipt for this purchase.' },
  { item: 'LEGIBLE_INVOICE', label: 'Clearer copy of the invoice', reason: 'Please upload a clearer copy of the invoice so it can be read.' },
  { item: 'PHOTO_OF_DAMAGE', label: 'Photos of the damage', reason: 'Please upload photos that clearly show the problem with the product.' },
  { item: 'PHOTO_OF_SERIAL_LABEL', label: 'Photo of the serial number label', reason: 'Please upload a photo of the label showing the serial number.' },
  { item: 'PURCHASE_DATE', label: 'Purchase date', reason: 'Please confirm the date you bought the product.' },
  { item: 'PROBLEM_DETAILS', label: 'More detail about the problem', reason: 'Please describe the problem in more detail.' },
  { item: 'OTHER', label: 'Something else', reason: '' },
]

/** Items the AI said were missing, preselected in the picker when it named any. */
export function suggestedRequestedItems(claim: ClaimDetail): RequestedItemCode[] {
  const codes = new Set(requestedItemOptions.map((option) => option.item as string))
  const missing = claim.latestEvaluation?.recommendation?.missingInformation ?? []
  return [...new Set(missing.map((entry) => entry.item).filter((item) => codes.has(item)))] as RequestedItemCode[]
}

/** The requested items to send: the preset reason per item, the reviewer's wording for `OTHER`. */
export function buildRequestedItems(selected: readonly RequestedItemCode[], otherReason: string): RequestedItem[] {
  return requestedItemOptions
    .filter((option) => selected.includes(option.item))
    .map((option) => ({ item: option.item, reason: option.item === 'OTHER' ? otherReason.trim() : option.reason }))
}
