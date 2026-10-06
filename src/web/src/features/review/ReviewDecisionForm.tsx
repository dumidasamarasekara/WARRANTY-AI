import { useState, type FormEvent, type MouseEvent, type ReactNode } from 'react'
import { ApiProblem, type Schemas } from '../../shared/api/client'
import { Alert, Button, Dialog, TextArea, TextInput } from '../../shared/ui'
import {
  aiDecisionLabel,
  buildRequestedItems,
  claimantMessageLimits,
  claimantMessagePrefill,
  decidingAiDecision,
  justificationLimits,
  overridesAi,
  requestedItemOptions,
  requiresClaimantMessage,
  requiresJustification,
  reviewDecisionLabel,
  suggestedRequestedItems,
  withinLimits,
  type ClaimDetail,
  type RequestedItemCode,
  type ReviewDecisionKind,
} from './reviewDecision'
import { useRecordReviewDecision } from './reviewQueries'
import styles from './ReviewDecisionForm.module.css'

const otherReasonMax = 500

export interface ReviewDecisionFormProps {
  claim: ClaimDetail
  /** ETag of `claim`, sent as `If-Match`. */
  etag: string
  decision: ReviewDecisionKind
  onClose: () => void
  onDecided: (decision: Schemas['ReviewDecision']) => void
  /** 403: the reviewer submitted this claim (separation of duties). */
  onSelfReviewRefused: () => void
  /** 409: the claim was decided elsewhere; refresh the queue. */
  onAlreadyDecided: () => void
  /** 412: reload the claim for a fresh ETag; the dialog keeps what the reviewer typed. */
  onReload: () => Promise<unknown>
}

function dialogTitle(decision: ReviewDecisionKind, overriding: boolean): string {
  if (decision === 'RequestInformation') return 'Request more information'
  if (overriding) return 'Override AI recommendation'
  return decision === 'Approve' ? 'Approve claim' : 'Reject claim'
}

/** A failed submission: the API's ProblemDetails, or a network failure without a status. */
type DecisionProblem = Pick<ApiProblem, 'title' | 'detail'> & { status?: number; errors?: Record<string, string[]> }

const confirmLabels: Record<ReviewDecisionKind, string> = {
  Approve: 'Approve claim',
  Reject: 'Reject claim',
  RequestInformation: 'Request information',
}

/** First message of a ValidationProblemDetails entry whose key names the field (any casing or path prefix). */
function fieldError(problem: DecisionProblem | null, field: string): string | undefined {
  if (problem?.status !== 400 || !problem.errors) return undefined
  const key = Object.keys(problem.errors).find((name) => name.toLowerCase().endsWith(field.toLowerCase()))
  return key ? problem.errors[key]?.join(' ') : undefined
}

/**
 * The decision dialog of the review workspace (ui-design.md §6.4, FR-034 – FR-036): Approve and
 * Reject always carry a message to the claimant, pre-filled only from a matching, valid and
 * `CLAIMANT_TEXT_SAFE` AI explanation; overrides and rejections need an internal reason; information
 * requests pick the requested items and carry no claimant message. Submits with `If-Match`.
 */
export function ReviewDecisionForm({
  claim,
  etag,
  decision,
  onClose,
  onDecided,
  onSelfReviewRefused,
  onAlreadyDecided,
  onReload,
}: ReviewDecisionFormProps) {
  const ai = decidingAiDecision(claim)
  const overriding = overridesAi(claim, decision)
  const needsMessage = requiresClaimantMessage(decision)
  const needsJustification = requiresJustification(claim, decision)
  const requestsInformation = decision === 'RequestInformation'

  const [message, setMessage] = useState(() => (needsMessage ? claimantMessagePrefill(claim, decision) : ''))
  const [justification, setJustification] = useState('')
  const [items, setItems] = useState<RequestedItemCode[]>(() => (requestsInformation ? suggestedRequestedItems(claim) : []))
  const [otherReason, setOtherReason] = useState('')
  const [problem, setProblem] = useState<DecisionProblem | null>(null)
  const [reloading, setReloading] = useState(false)
  const record = useRecordReviewDecision()

  const otherSelected = items.includes('OTHER')
  const valid =
    (!needsMessage || withinLimits(message, claimantMessageLimits)) &&
    (!needsJustification || withinLimits(justification, justificationLimits)) &&
    (!requestsInformation ||
      (items.length > 0 && (!otherSelected || (otherReason.trim().length > 0 && otherReason.trim().length <= otherReasonMax))))

  // A 400 naming a disclosure term belongs to the message field (FR-036); other field errors to theirs.
  const messageError = fieldError(problem, 'claimantExplanation')
  const justificationError = fieldError(problem, 'justification')
  const itemsError = fieldError(problem, 'requestedItems')

  const submit = (event: FormEvent | MouseEvent) => {
    event.preventDefault()
    if (!valid || record.isPending) return
    setProblem(null)
    record.mutate(
      {
        claimId: claim.claimId,
        etag,
        request: {
          decision,
          justification: needsJustification ? justification.trim() : undefined,
          claimantExplanation: needsMessage ? message.trim() : undefined,
          requestedItems: requestsInformation ? buildRequestedItems(items, otherReason) : undefined,
        },
      },
      {
        onSuccess: onDecided,
        onError: (error) => {
          const failure = error instanceof ApiProblem ? error : null
          if (failure?.status === 403) {
            onSelfReviewRefused()
            return
          }
          setProblem(failure ?? { title: 'The decision could not be recorded', detail: 'Check your connection and try again.' })
        },
      },
    )
  }

  const reload = async () => {
    setReloading(true)
    try {
      await onReload()
      setProblem(null)
    } finally {
      setReloading(false)
    }
  }

  let error: ReactNode = null
  if (problem?.status === 409) {
    error = (
      <div className={styles.problem}>
        <span>This claim has already been decided.</span>
        <Button onClick={onAlreadyDecided}>Refresh</Button>
      </div>
    )
  } else if (problem?.status === 412) {
    error = (
      <div className={styles.problem}>
        <span>The claim changed since you opened it — reload.</span>
        <Button onClick={reload} loading={reloading}>
          Reload
        </Button>
      </div>
    )
  } else if (problem && !messageError && !justificationError && !itemsError) {
    error = [problem.title, problem.detail].filter(Boolean).join(' — ') || 'The decision could not be recorded.'
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title={dialogTitle(decision, overriding)}
      submitting={record.isPending}
      error={error}
      footer={
        <>
          <Button onClick={onClose} disabled={record.isPending}>
            Cancel
          </Button>
          <Button
            onClick={submit}
            variant={overriding ? 'human' : decision === 'Reject' ? 'danger' : 'primary'}
            disabled={!valid}
            loading={record.isPending}
          >
            {confirmLabels[decision]}
          </Button>
        </>
      }
    >
      <form className={styles.form} onSubmit={submit} noValidate>
        {needsJustification && (
          <p className={styles.summary}>
            {ai && (
              <>
                AI recommends <b>{aiDecisionLabel(ai)}</b>. You are choosing <b>{reviewDecisionLabel(decision)}</b>.{' '}
              </>
            )}
            This is recorded as a human decision with your reason, linked to the decision trace.
          </p>
        )}

        {requestsInformation && (
          <>
            <fieldset className={styles.items} aria-describedby={itemsError ? 'requested-items-error' : undefined}>
              <legend className={styles.legend}>
                Information to request<span aria-hidden="true"> *</span>
              </legend>
              {requestedItemOptions.map((option) => (
                <label key={option.item} className={styles.item}>
                  <input
                    type="checkbox"
                    checked={items.includes(option.item)}
                    onChange={(event) =>
                      setItems((current) =>
                        event.target.checked ? [...current, option.item] : current.filter((item) => item !== option.item),
                      )
                    }
                  />
                  <span>{option.label}</span>
                </label>
              ))}
              {itemsError && (
                <p id="requested-items-error" className={styles.fieldError}>
                  {itemsError}
                </p>
              )}
            </fieldset>
            {otherSelected && (
              <TextInput
                label="What else do you need?"
                hint="Shown to the claimant."
                required
                maxLength={otherReasonMax}
                value={otherReason}
                onChange={(event) => setOtherReason(event.target.value)}
              />
            )}
            <Alert tone="warn">After the customer responds, this claim returns to the review queue.</Alert>
          </>
        )}

        {needsMessage && (
          <TextArea
            label="Message to the claimant"
            hint="Shown to the claimant. Don't mention risk or fraud checks."
            required
            rows={5}
            characterLimits={claimantMessageLimits}
            error={messageError}
            value={message}
            onChange={(event) => {
              setMessage(event.target.value)
              if (messageError) setProblem(null)
            }}
          />
        )}

        {needsJustification && (
          <TextArea
            label="Internal reason — never shown to the claimant"
            required
            rows={4}
            characterLimits={justificationLimits}
            error={justificationError}
            value={justification}
            onChange={(event) => {
              setJustification(event.target.value)
              if (justificationError) setProblem(null)
            }}
          />
        )}
      </form>
    </Dialog>
  )
}
