import { useState, type FormEvent, type MouseEvent, type ReactNode } from 'react'
import { ApiProblem } from '../../shared/api/client'
import { Button, Dialog, Select, TextArea, TextInput } from '../../shared/ui'
import {
  useRecordReplacementEvent,
  useReplacementLocations,
  type ReplacementEventRequest,
  type ReplacementView,
  type ReportSource,
} from './remedyQueries'
import { reportSourceLabel, reportSources } from './remedyView'
import { needsReturnConfirmation, replacementActionLabels, replacementReasonLimits, type ReplacementDialogEvent } from './replacementView'
import styles from './RemedyTab.module.css'

/** The model a reserved unit must be: the claimed one, or the model a reviewer approved. */
export interface UnitModel {
  modelCode: string
  name?: string
}

export interface ReplacementEventDialogProps {
  claimId: string
  replacement: ReplacementView
  type: ReplacementDialogEvent
  /** The claimed product's model; a reserved unit is this model unless a reviewer approved another. */
  claimedModel: UnitModel
  onClose: () => void
  onRecorded: (replacement: ReplacementView) => void
  /** 409 and 412: reload the claim for the current replacement and a fresh ETag; the dialog keeps the input. */
  onReload: () => Promise<unknown>
}

type EventProblem = { status?: number; title?: string; detail?: string; errors?: Record<string, string[]> }

function fieldError(problem: EventProblem | null, field: string): string | undefined {
  if (problem?.status !== 400 || !problem.errors) return undefined
  const key = Object.keys(problem.errors).find((name) => name.toLowerCase().endsWith(field.toLowerCase()))
  return key ? problem.errors[key]?.join(' ') : undefined
}

const fields = ['reportedAt', 'source', 'note', 'locationId', 'reason', 'serialNumber', 'handedOverOn', 'returnConfirmed'] as const

const pad = (value: number) => String(value).padStart(2, '0')

/** `datetime-local` value of a local time, minutes precision. */
const localDateTime = (date: Date) =>
  `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`

const localDate = (date: Date) => localDateTime(date).slice(0, 10)

const modelText = (model: UnitModel) => (model.name ? `${model.name} (${model.modelCode})` : model.modelCode)

const descriptions: Record<ReplacementDialogEvent, string> = {
  LocationChanged: 'Choose another location to issue the replacement.',
  UnitReserved: 'Record the unit the location reserved for the customer.',
  HandedToCustomer: 'Record that the customer collected the replacement.',
}

/**
 * One dialog per replacement event (ui-screens.md §2): reported date-time (default now, not in the future), source,
 * the event's own fields and an optional note. Submits with `If-Match` = the replacement ETag.
 */
export function ReplacementEventDialog({
  claimId,
  replacement,
  type,
  claimedModel,
  onClose,
  onRecorded,
  onReload,
}: ReplacementEventDialogProps) {
  // The latest time the user acted: the bound for 'not in the future' (refreshed in event handlers).
  const [now, setNow] = useState(() => new Date())
  const [reportedAt, setReportedAt] = useState(() => localDateTime(now))
  const [source, setSource] = useState<ReportSource | ''>('')
  const [note, setNote] = useState('')
  const [locationId, setLocationId] = useState('')
  const [reason, setReason] = useState('')
  const [serialNumber, setSerialNumber] = useState('')
  const [handedOverOn, setHandedOverOn] = useState(() => localDate(now))
  const [returnConfirmed, setReturnConfirmed] = useState(false)
  const [problem, setProblem] = useState<EventProblem | null>(null)
  const [reloading, setReloading] = useState(false)
  const record = useRecordReplacementEvent()
  const choosesLocation = type === 'LocationChanged'
  const locations = useReplacementLocations(claimId, choosesLocation)
  const confirmsReturn = type === 'HandedToCustomer' && needsReturnConfirmation(replacement)
  const unitModel: UnitModel = replacement.approvedModel ?? claimedModel

  const title = replacementActionLabels[type]
  const reported = reportedAt ? new Date(reportedAt) : undefined
  const reportedValid = reported !== undefined && !Number.isNaN(reported.getTime()) && reported.getTime() <= now.getTime()
  const trimmedReason = reason.trim()
  const trimmedNote = note.trim()
  const trimmedSerial = serialNumber.trim()
  const reasonValid = trimmedReason.length >= replacementReasonLimits.min && trimmedReason.length <= replacementReasonLimits.max

  let eventValid = true
  if (choosesLocation) eventValid = locationId !== '' && reasonValid
  if (type === 'UnitReserved') eventValid = trimmedSerial !== ''
  if (type === 'HandedToCustomer') eventValid = handedOverOn !== '' && handedOverOn <= localDate(now) && (!confirmsReturn || returnConfirmed)
  const valid = reportedValid && source !== '' && trimmedNote.length <= replacementReasonLimits.max && eventValid

  const errors = Object.fromEntries(fields.map((field) => [field, fieldError(problem, field)])) as Record<
    (typeof fields)[number],
    string | undefined
  >
  const clearFieldError = (field: (typeof fields)[number]) => {
    if (errors[field]) setProblem(null)
  }

  const submit = (event: FormEvent | MouseEvent) => {
    event.preventDefault()
    if (!valid || !reported || record.isPending) return
    const request: ReplacementEventRequest = { type, reportedAt: reported.toISOString(), source }
    if (trimmedNote) request.note = trimmedNote
    if (choosesLocation) {
      request.locationId = locationId
      request.reason = trimmedReason
    }
    if (type === 'UnitReserved') request.serialNumber = trimmedSerial
    if (type === 'HandedToCustomer') {
      request.handedOverOn = handedOverOn
      if (confirmsReturn) request.returnConfirmed = true
    }
    setProblem(null)
    record.mutate(
      { claimId, etag: replacement.etag, request },
      {
        onSuccess: onRecorded,
        onError: (error) =>
          setProblem(error instanceof ApiProblem ? error : { title: 'The update could not be recorded', detail: 'Check your connection and try again.' }),
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
      <div className={styles.problem}>
        <span>{problem.detail || problem.title || 'This update is not allowed in the replacement’s current status.'}</span>
        {reloadButton}
      </div>
    )
  } else if (problem?.status === 412) {
    error = (
      <div className={styles.problem}>
        <span>This replacement changed since you opened it. Reload to see the latest status.</span>
        {reloadButton}
      </div>
    )
  } else if (problem?.status === 403) {
    error = problem.detail || 'You are not allowed to record this update. Ask a claims reviewer.'
  } else if (problem && !Object.values(errors).some(Boolean)) {
    error = [problem.title, problem.detail].filter(Boolean).join(' — ') || 'The update could not be recorded.'
  }

  const otherLocations = (locations.data ?? []).filter((location) => location.id !== replacement.activeLocation?.id)

  return (
    <Dialog
      open
      onClose={onClose}
      title={title}
      description={descriptions[type]}
      submitting={record.isPending}
      error={error}
      footer={
        <>
          <Button onClick={onClose} disabled={record.isPending}>
            Cancel
          </Button>
          <Button variant="primary" onClick={submit} disabled={!valid} loading={record.isPending}>
            {title}
          </Button>
        </>
      }
    >
      <form className={styles.form} onSubmit={submit} noValidate>
        <TextInput
          type="datetime-local"
          label="Reported"
          hint="When the location reported it; not in the future."
          required
          max={localDateTime(now)}
          value={reportedAt}
          error={errors.reportedAt ?? (reportedAt && !reportedValid ? 'The reported time cannot be in the future.' : undefined)}
          onChange={(event) => {
            setReportedAt(event.target.value)
            setNow(new Date())
            clearFieldError('reportedAt')
          }}
        />
        <Select
          label="Source"
          required
          value={source}
          error={errors.source}
          onChange={(event) => {
            setSource(event.target.value as ReportSource | '')
            clearFieldError('source')
          }}
        >
          <option value="">Choose a source</option>
          {reportSources.map((option) => (
            <option key={option} value={option}>
              {reportSourceLabel(option)}
            </option>
          ))}
        </Select>

        {choosesLocation && (
          <>
            <Select
              label="New location"
              required
              value={locationId}
              disabled={locations.isPending}
              error={errors.locationId ?? (locations.isError ? 'The locations could not be loaded.' : undefined)}
              onChange={(event) => {
                setLocationId(event.target.value)
                clearFieldError('locationId')
              }}
            >
              <option value="">{locations.isPending ? 'Loading locations…' : 'Choose a location'}</option>
              {otherLocations.map((location) => (
                <option key={location.id} value={location.id}>
                  {location.name}
                  {location.suggested ? ' (suggested)' : ''}
                  {location.servesClaim ? '' : ' — does not serve this claim'}
                </option>
              ))}
            </Select>
            <TextArea
              label="Reason"
              hint="Internal — never shown to the claimant."
              required
              rows={3}
              characterLimits={replacementReasonLimits}
              value={reason}
              error={errors.reason}
              onChange={(event) => {
                setReason(event.target.value)
                clearFieldError('reason')
              }}
            />
          </>
        )}

        {type === 'UnitReserved' && (
          <>
            <p className={styles.note}>
              Model: {modelText(unitModel)}
              {replacement.approvedModel ? ' (approved equivalent)' : ''}
            </p>
            <TextInput
              label="Serial number of the reserved unit"
              hint="As printed on the unit. It must differ from the claimed product’s serial."
              required
              value={serialNumber}
              error={errors.serialNumber}
              onChange={(event) => {
                setSerialNumber(event.target.value)
                clearFieldError('serialNumber')
              }}
            />
            <p className={styles.note}>The customer is notified that the replacement is ready for collection.</p>
          </>
        )}

        {type === 'HandedToCustomer' && (
          <>
            <TextInput
              type="date"
              label="Handed to customer on"
              required
              max={localDate(now)}
              value={handedOverOn}
              error={errors.handedOverOn}
              onChange={(event) => {
                setHandedOverOn(event.target.value)
                setNow(new Date())
                clearFieldError('handedOverOn')
              }}
            />
            {confirmsReturn && (
              <div className={styles.choices}>
                <label className={styles.choice}>
                  <input
                    type="checkbox"
                    required
                    checked={returnConfirmed}
                    aria-describedby={errors.returnConfirmed ? 'return-confirmed-error' : undefined}
                    onChange={(event) => {
                      setReturnConfirmed(event.target.checked)
                      clearFieldError('returnConfirmed')
                    }}
                  />
                  <span>
                    Faulty product returned<span aria-hidden="true"> *</span>
                  </span>
                </label>
                <p className={styles.note}>The policy requires the faulty product back when the replacement is collected.</p>
                {errors.returnConfirmed && (
                  <p id="return-confirmed-error" className={styles.fieldError}>
                    {errors.returnConfirmed}
                  </p>
                )}
              </div>
            )}
            <p className={styles.note}>This completes the remedy and registers the unit’s serial for later claims.</p>
          </>
        )}

        <TextArea
          label="Note (optional)"
          hint="Internal — never shown to the claimant."
          rows={3}
          characterLimits={{ max: replacementReasonLimits.max }}
          value={note}
          error={errors.note}
          onChange={(event) => {
            setNote(event.target.value)
            clearFieldError('note')
          }}
        />
      </form>
    </Dialog>
  )
}
