import { useState, type FormEvent, type ReactNode } from 'react'
import { ApiProblem } from '../../shared/api/client'
import { formatDate } from '../../shared/presentation'
import { Alert, Badge, Button, Card, TextInput } from '../../shared/ui'
import { useRecordRemedyCompletion, type RemedyCompletionRequest, type RemedyView } from './remedyQueries'
import { completionCopy, type CompletableRemedy } from './remedyView'
import styles from './RemedyTab.module.css'

export interface CompletionCardProps {
  claimId: string
  /** A Replacement or Refund remedy that is `InProgress` or `Completed`. */
  remedy: RemedyView & { remedy: CompletableRemedy }
  /** Claims agents and reviewers record the completion; auditors see the card read-only. */
  canAct: boolean
  /** Reload the claim for the current remedy and a fresh ETag (409 and 412). */
  onReload: () => Promise<unknown>
}

type CompletionProblem = { status?: number; title?: string; detail?: string; errors?: Record<string, string[]> }

function fieldError(problem: CompletionProblem | null, field: string): string | undefined {
  if (problem?.status !== 400 || !problem.errors) return undefined
  const key = Object.keys(problem.errors).find((name) => name.toLowerCase().endsWith(field.toLowerCase()))
  return key ? problem.errors[key]?.join(' ') : undefined
}

const pad = (value: number) => String(value).padStart(2, '0')

/** `date` input value of a local day. */
const localDate = (date: Date) => `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`

function CompletionForm({ claimId, remedy, onReload, onRecorded }: Omit<CompletionCardProps, 'canAct'> & { onRecorded: () => void }) {
  const copy = completionCopy[remedy.remedy]
  const refund = remedy.remedy === 'Refund'
  // Today, refreshed whenever the user acts: the bound for 'not in the future'.
  const [today, setToday] = useState(() => localDate(new Date()))
  const [completedOn, setCompletedOn] = useState(today)
  const [reference, setReference] = useState('')
  const [problem, setProblem] = useState<CompletionProblem | null>(null)
  const [reloading, setReloading] = useState(false)
  const record = useRecordRemedyCompletion()

  const trimmedReference = reference.trim()
  const dateInFuture = completedOn !== '' && completedOn > today
  const valid = completedOn !== '' && !dateInFuture && (!refund || trimmedReference !== '')

  const errors = { completedOn: fieldError(problem, 'completedOn'), reference: fieldError(problem, 'reference') }

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (!valid || record.isPending) return
    const request: RemedyCompletionRequest = { remedy: remedy.remedy, completedOn }
    if (trimmedReference) request.reference = trimmedReference
    setProblem(null)
    record.mutate(
      { claimId, etag: remedy.etag, request },
      {
        onSuccess: onRecorded,
        onError: (error) =>
          setProblem(error instanceof ApiProblem ? error : { title: 'The completion could not be recorded', detail: 'Check your connection and try again.' }),
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
  if (problem?.status === 409) {
    error = (
      <Alert tone="err" urgent action={reloadButton}>
        {problem.detail || problem.title || 'This remedy can no longer be completed in its current state.'}
      </Alert>
    )
  } else if (problem?.status === 412) {
    error = (
      <Alert tone="err" urgent action={reloadButton}>
        This remedy changed since you opened it. Reload to see the latest status.
      </Alert>
    )
  } else if (problem && !errors.completedOn && !errors.reference) {
    error = (
      <Alert tone="err" urgent>
        {[problem.title, problem.detail].filter(Boolean).join(' — ') || 'The completion could not be recorded.'}
      </Alert>
    )
  }

  return (
    <form className={styles.form} onSubmit={submit} noValidate aria-label="Record completion">
      <p className={styles.muted}>{copy.hint}</p>
      {error}
      <TextInput
        type="date"
        label={copy.dateLabel}
        hint="Not in the future."
        required
        max={today}
        value={completedOn}
        error={errors.completedOn ?? (dateInFuture ? 'The date cannot be in the future.' : undefined)}
        onChange={(event) => {
          setCompletedOn(event.target.value)
          setToday(localDate(new Date()))
          if (errors.completedOn) setProblem(null)
        }}
      />
      <TextInput
        label={refund ? 'Refund reference' : 'Reference (optional)'}
        hint={refund ? 'The reference the refund option gave for this refund.' : undefined}
        required={refund}
        value={reference}
        error={errors.reference}
        onChange={(event) => {
          setReference(event.target.value)
          if (errors.reference) setProblem(null)
        }}
      />
      <div className={styles.actions}>
        <Button type="submit" variant="primary" disabled={!valid} loading={record.isPending}>
          Record completion
        </Button>
      </div>
    </form>
  )
}

/**
 * Left column of the Remedy tab when the remedy is Replacement or Refund (002 ui-screens.md §2,
 * FR-034, FR-035): while `InProgress`, claims agents and reviewers record the completion date (and the
 * refund reference); once `Completed`, a read-only summary. Submits with `If-Match` = the remedy ETag.
 */
export function CompletionCard({ claimId, remedy, canAct, onReload }: CompletionCardProps) {
  const copy = completionCopy[remedy.remedy]
  const completion = remedy.completion
  const completed = remedy.state === 'Completed'
  const [recorded, setRecorded] = useState(false)

  return (
    <>
      {recorded && completed && <Alert tone="ok" title="Completion recorded" />}
      <Card title="Completion" titleAs="h2">
        <div className={styles.stack}>
          {completed ? (
            <>
              <div className={styles.current}>
                <Badge tone="ok" dot>
                  {copy.done}
                </Badge>
              </div>
              <dl className={styles.facts}>
                <div>
                  <dt>Completed on</dt>
                  <dd>{completion?.completedOn ? formatDate(completion.completedOn) : '—'}</dd>
                </div>
                <div>
                  <dt>Reference</dt>
                  <dd className={styles.mono}>{completion?.reference ?? '—'}</dd>
                </div>
                <div>
                  <dt>Recorded by</dt>
                  <dd>{completion?.recordedBy ?? '—'}</dd>
                </div>
              </dl>
            </>
          ) : canAct ? (
            <CompletionForm claimId={claimId} remedy={remedy} onReload={onReload} onRecorded={() => setRecorded(true)} />
          ) : (
            <p className={styles.muted}>{copy.pending}</p>
          )}
        </div>
      </Card>
    </>
  )
}
