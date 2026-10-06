import { useMutation } from '@tanstack/react-query'
import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { ApiProblem } from '../../shared/api/client'
import { requestedItemLabel } from '../../shared/presentation'
import { Alert, Button, FileDrop, TextArea, type FileDropItem } from '../../shared/ui'
import { acceptedFiles, checkFile, fileHint, maxPhotos, type SubmissionAccepted } from './claimSubmission'
import {
  buildSupplementFormData,
  supplementFieldForProblemKey,
  supplementNoteMax,
  validateSupplement,
  type RequestedItem,
  type SupplementErrors,
  type SupplementField,
} from './supplement'
import styles from './SupplementForm.module.css'

export type SupplementFormVariant = 'claimant' | 'staff'

export interface SupplementFormProps {
  /** `claimant`: portal copy and 40 px controls; `staff`: agent copy and 36 px controls, in a dialog. */
  variant: SupplementFormVariant
  /** The claim's requested items, listed above the fields. */
  requestedItems: readonly RequestedItem[]
  /** Posts the multipart `SupplementForm`; rejects with an {@link ApiProblem} on failure. */
  submit: (body: FormData) => Promise<SubmissionAccepted>
  onSubmitted: (accepted: SubmissionAccepted) => void
  /** Shows a Cancel button (staff dialog). */
  onCancel?: () => void
  /** Lets a surrounding dialog block closing while the supplement is being sent. */
  onPendingChange?: (pending: boolean) => void
}

const copy = {
  claimant: {
    requested: 'What we need',
    note: 'Anything else you want to tell us?',
    noteHint: 'Optional. For example the date you bought the product.',
    invoice: 'Invoice or receipt',
    photos: `Photos of the product (up to ${maxPhotos})`,
    submit: 'Send information',
    conflict: 'This claim is no longer waiting for information',
  },
  staff: {
    requested: 'Requested from the customer',
    note: 'Note',
    noteHint: 'Optional. What the customer told you, e.g. the purchase date.',
    invoice: 'Invoice',
    photos: `Photos (up to ${maxPhotos})`,
    submit: 'Add supplement',
    conflict: 'The claim is no longer Pending information',
  },
} as const

interface PickedFile {
  key: string
  file: File
}

let nextFileKey = 0
const pick = (file: File): PickedFile => ({ key: `supplement-${++nextFileKey}`, file })
const toItem = ({ key, file }: PickedFile): FileDropItem => ({ key, name: file.name, size: file.size, status: 'done' })

function withError(errors: SupplementErrors, field: SupplementField | 'form', message: string | undefined): SupplementErrors {
  const next = { ...errors }
  if (message) next[field] = message
  else delete next[field]
  return next
}

function fieldErrorsOf(error: unknown): SupplementErrors {
  const errors: SupplementErrors = {}
  if (!(error instanceof ApiProblem)) return errors
  for (const [key, messages] of Object.entries(error.errors)) {
    const field = supplementFieldForProblemKey(key)
    if (field && messages[0] && !errors[field]) errors[field] = messages[0]
  }
  return errors
}

/** A failed supplement: ProblemDetails title and detail, unattached field errors, correlation ID (ui-design.md §8). */
function SupplementProblem({ error, conflictTitle }: { error: unknown; conflictTitle: string }) {
  if (!(error instanceof ApiProblem)) {
    return (
      <Alert tone="err" urgent title="We couldn't send the information">
        Check your connection and try again.
      </Alert>
    )
  }
  const unattached = Object.entries(error.errors)
    .filter(([key]) => !supplementFieldForProblemKey(key))
    .flatMap(([, messages]) => messages)
  const title = error.status === 409 ? conflictTitle : (error.title ?? "We couldn't send the information")
  return (
    <Alert tone="err" urgent title={title}>
      {error.detail && <p>{error.detail}</p>}
      {unattached.length > 0 && (
        <ul className={styles.problemList}>
          {unattached.map((message) => (
            <li key={message}>{message}</li>
          ))}
        </ul>
      )}
      {error.correlationId && (
        <p className={styles.correlation}>
          Correlation ID <code>{error.correlationId}</code>
        </p>
      )}
    </Alert>
  )
}

/**
 * Supplements a claim that is Pending information (FR-010): lists the requested items and takes a
 * note, the invoice and up to 8 photos, at least one of them. Files are checked like the claim
 * form before sending; the API's field errors are attached to their fields. Used on the claimant's
 * status page and in the staff "Add supplement" dialog (ui-design.md §6.3, §6.6).
 */
export function SupplementForm({ variant, requestedItems, submit, onSubmitted, onCancel, onPendingChange }: SupplementFormProps) {
  const baseId = useId()
  const text = copy[variant]
  const size = variant === 'claimant' ? 'lg' : 'md'
  const [note, setNote] = useState('')
  const [invoice, setInvoice] = useState<PickedFile | null>(null)
  const [photos, setPhotos] = useState<PickedFile[]>([])
  const [errors, setErrors] = useState<SupplementErrors>({})
  const [focusRequest, setFocusRequest] = useState(0)
  const formRef = useRef<HTMLFormElement>(null)

  useEffect(() => {
    if (focusRequest > 0) formRef.current?.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus()
  }, [focusRequest])

  const submission = useMutation({
    mutationFn: (body: FormData) => submit(body),
    onSuccess: onSubmitted,
    onError: (error) => {
      const fieldErrors = fieldErrorsOf(error)
      if (Object.keys(fieldErrors).length === 0) return
      setErrors(fieldErrors)
      setFocusRequest((previous) => previous + 1)
    },
  })

  const pending = submission.isPending
  useEffect(() => onPendingChange?.(pending), [onPendingChange, pending])

  function pickInvoice(files: File[]) {
    const file = files[0]
    if (!file) return
    const problem = checkFile(file)
    if (!problem) setInvoice(pick(file))
    setErrors((current) => withError(withError(current, 'invoice', problem), 'form', undefined))
  }

  function addPhotos(files: File[]) {
    const accepted: PickedFile[] = []
    let problem: string | undefined
    for (const file of files) {
      const fileProblem = checkFile(file)
      if (fileProblem) problem ??= fileProblem
      else accepted.push(pick(file))
    }
    const room = Math.max(0, maxPhotos - photos.length)
    if (accepted.length > room) {
      problem ??= `You can add up to ${maxPhotos} photos`
      accepted.length = room
    }
    setPhotos([...photos, ...accepted])
    setErrors((current) => withError(withError(current, 'photos', problem), 'form', undefined))
  }

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (pending) return
    const pickedPhotos = photos.map((photo) => photo.file)
    const found = validateSupplement(note, invoice?.file ?? null, pickedPhotos)
    setErrors(found)
    if (Object.keys(found).length > 0) {
      setFocusRequest((previous) => previous + 1)
      return
    }
    submission.mutate(buildSupplementFormData(note, invoice?.file ?? null, pickedPhotos))
  }

  return (
    <form ref={formRef} className={styles.form} onSubmit={handleSubmit} noValidate aria-label={text.submit}>
      {requestedItems.length > 0 && (
        <section className={styles.requested} aria-labelledby={`${baseId}-requested`}>
          <h3 id={`${baseId}-requested`} className={styles.requestedTitle}>
            {text.requested}
          </h3>
          <ul className={styles.requestedList}>
            {requestedItems.map((requested, index) => (
              <li key={`${index}-${requested.item}`}>
                <strong>{requestedItemLabel(requested.item)}</strong>
                <span className={styles.reason}>{requested.reason}</span>
              </li>
            ))}
          </ul>
        </section>
      )}

      <TextArea
        id={`${baseId}-note`}
        label={text.note}
        hint={text.noteHint}
        size={size}
        rows={4}
        characterLimits={{ max: supplementNoteMax }}
        value={note}
        error={errors.note}
        disabled={pending}
        onChange={(event) => {
          setNote(event.target.value)
          setErrors((current) => withError(withError(current, 'note', undefined), 'form', undefined))
        }}
      />
      <FileDrop
        label={text.invoice}
        hint={`Optional · ${fileHint}`}
        accept={acceptedFiles}
        files={invoice ? [toItem(invoice)] : []}
        error={errors.invoice}
        disabled={pending}
        onFiles={pickInvoice}
        onRemove={() => setInvoice(null)}
      />
      <FileDrop
        label={text.photos}
        hint={`Optional · ${fileHint}`}
        accept={acceptedFiles}
        multiple
        files={photos.map(toItem)}
        error={errors.photos}
        disabled={pending}
        onFiles={addPhotos}
        onRemove={(key) => setPhotos(photos.filter((photo) => photo.key !== key))}
      />
      {variant === 'claimant' && (
        <p className={styles.note}>Your documents are stored securely and used only to process this claim.</p>
      )}

      {errors.form && (
        <Alert tone="err" urgent>
          {errors.form}
        </Alert>
      )}
      {submission.isError && <SupplementProblem error={submission.error} conflictTitle={text.conflict} />}

      <div className={styles.actions}>
        {onCancel && (
          <Button size={size} className={styles.action} disabled={pending} onClick={onCancel}>
            Cancel
          </Button>
        )}
        <Button type="submit" variant="primary" size={size} className={styles.action} loading={pending}>
          {text.submit}
        </Button>
      </div>
    </form>
  )
}
