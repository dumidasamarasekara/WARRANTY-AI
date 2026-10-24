import { useState, type FormEvent, type MouseEvent, type ReactNode } from 'react'
import { ApiProblem } from '../../shared/api/client'
import type { RemedyType } from '../../shared/presentation'
import { Button, Dialog, TextArea } from '../../shared/ui'
import { useDecideRemedy, type RemedyView } from './remedyQueries'
import { pendingReasonText, reasonRequired, remedyLabel, remedyReasonLimits } from './remedyView'
import styles from './RemedyTab.module.css'

export interface RemedyDialogProps {
  claimId: string
  remedy: RemedyView
  onClose: () => void
  onDecided: (remedy: RemedyView) => void
  /** 409 and 412: reload the claim for its current remedy and a fresh ETag; the dialog keeps the input. */
  onReload: () => Promise<unknown>
}

type DecisionProblem = { status?: number; title?: string; detail?: string; errors?: Record<string, string[]> }

/** First message of a ValidationProblemDetails entry whose key names the field (any casing or path prefix). */
function fieldError(problem: DecisionProblem | null, field: string): string | undefined {
  if (problem?.status !== 400 || !problem.errors) return undefined
  const key = Object.keys(problem.errors).find((name) => name.toLowerCase().endsWith(field.toLowerCase()))
  return key ? problem.errors[key]?.join(' ') : undefined
}

const problemText = (problem: DecisionProblem) => [problem.title, problem.detail].filter(Boolean).join(' — ')

/**
 * **Decide remedy** (ui-screens.md §4): a radio group of only `RemedyView.allowedRemedies`, pre-selected
 * with the recommendation when there is one, a reason (required except for the first decision of a policy
 * choice) and the note that only allowed remedies are listed. Submits with `If-Match` = the remedy ETag.
 */
export function RemedyDialog({ claimId, remedy, onClose, onDecided, onReload }: RemedyDialogProps) {
  const [choice, setChoice] = useState<RemedyType | undefined>(() =>
    remedy.recommendedRemedy && remedy.allowedRemedies.includes(remedy.recommendedRemedy) ? remedy.recommendedRemedy : undefined,
  )
  const [reason, setReason] = useState('')
  const [problem, setProblem] = useState<DecisionProblem | null>(null)
  const [reloading, setReloading] = useState(false)
  const decide = useDecideRemedy()

  const needsReason = reasonRequired(remedy)
  const trimmed = reason.trim()
  const reasonValid = trimmed.length === 0 ? !needsReason : trimmed.length >= remedyReasonLimits.min && trimmed.length <= remedyReasonLimits.max
  const valid = choice !== undefined && remedy.allowedRemedies.includes(choice) && reasonValid

  const remedyError = fieldError(problem, 'remedy')
  const reasonError = fieldError(problem, 'reason')

  const submit = (event: FormEvent | MouseEvent) => {
    event.preventDefault()
    if (!valid || !choice || decide.isPending) return
    setProblem(null)
    decide.mutate(
      { claimId, etag: remedy.etag, request: { remedy: choice, reason: trimmed.length > 0 ? trimmed : undefined } },
      {
        onSuccess: onDecided,
        onError: (error) =>
          setProblem(error instanceof ApiProblem ? error : { title: 'The remedy could not be recorded', detail: 'Check your connection and try again.' }),
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

  const reloadButton = (
    <Button onClick={reload} loading={reloading}>
      Reload
    </Button>
  )

  let error: ReactNode = null
  if (problem?.status === 403) {
    error = 'You submitted this claim, so another reviewer must decide its remedy.'
  } else if (problem?.status === 409) {
    error = (
      <div className={styles.problem}>
        <span>{problem.detail || problem.title || 'The remedy can no longer be decided in its current state.'}</span>
        {reloadButton}
      </div>
    )
  } else if (problem?.status === 412) {
    error = (
      <div className={styles.problem}>
        <span>This remedy changed since you opened it. Reload to see the latest status.</span>
        {reloadButton}
      </div>
    )
  } else if (problem && !remedyError && !reasonError) {
    error = problemText(problem) || 'The remedy could not be recorded.'
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title="Decide remedy"
      description={remedy.state === 'AwaitingDecision' ? pendingReasonText(remedy) : undefined}
      submitting={decide.isPending}
      error={error}
      footer={
        <>
          <Button onClick={onClose} disabled={decide.isPending}>
            Cancel
          </Button>
          <Button variant="primary" onClick={submit} disabled={!valid} loading={decide.isPending}>
            Decide remedy
          </Button>
        </>
      }
    >
      <form className={styles.form} onSubmit={submit} noValidate>
        <fieldset className={styles.choices} aria-describedby={remedyError ? 'remedy-choice-error' : 'remedy-choice-note'}>
          <legend className={styles.legend}>
            Remedy<span aria-hidden="true"> *</span>
          </legend>
          {remedy.allowedRemedies.map((option) => (
            <label key={option} className={styles.choice}>
              <input
                type="radio"
                name="remedy"
                value={option}
                checked={choice === option}
                onChange={() => {
                  setChoice(option)
                  if (remedyError) setProblem(null)
                }}
              />
              <span>{remedyLabel(option)}</span>
            </label>
          ))}
          <p id="remedy-choice-note" className={styles.note}>
            Only remedies the policy allows are listed.
          </p>
          {remedyError && (
            <p id="remedy-choice-error" className={styles.fieldError}>
              {remedyError}
            </p>
          )}
        </fieldset>
        <TextArea
          label={needsReason ? 'Reason' : 'Reason (optional)'}
          hint="Internal — never shown to the claimant."
          required={needsReason}
          rows={4}
          characterLimits={needsReason ? remedyReasonLimits : { max: remedyReasonLimits.max }}
          error={reasonError}
          value={reason}
          onChange={(event) => {
            setReason(event.target.value)
            if (reasonError) setProblem(null)
          }}
        />
      </form>
    </Dialog>
  )
}
