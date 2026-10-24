import { useState, type FormEvent, type MouseEvent, type ReactNode } from 'react'
import { useMe } from '../../app/staffQueries'
import { ApiProblem } from '../../shared/api/client'
import { formatDateTime, formatMoney, repairStatusPresentation } from '../../shared/presentation'
import { Button, Dialog, Select, TextArea, TextInput } from '../../shared/ui'
import {
  useRecordRepairEvent,
  useRepairCentres,
  type RepairEventRequest,
  type RepairStatus,
  type RepairView,
  type ReportSource,
} from './remedyQueries'
import {
  correctableFieldLabels,
  correctableFields,
  failedAttemptsText,
  isAboveEconomicLimit,
  productLocationLabels,
  repairActionLabels,
  repairEventName,
  repairReasonLimits,
  repairStatuses,
  reportSourceLabel,
  reportSources,
  type CorrectableField,
  type RepairDialogEvent,
} from './remedyView'
import styles from './RemedyTab.module.css'

export interface RepairEventDialogProps {
  claimId: string
  repair: RepairView
  type: RepairDialogEvent
  onClose: () => void
  onRecorded: (repair: RepairView) => void
  /** 409 and 412: reload the claim for the current repair and a fresh ETag; the dialog keeps the input. */
  onReload: () => Promise<unknown>
}

type EventProblem = { status?: number; title?: string; detail?: string; errors?: Record<string, string[]> }

function fieldError(problem: EventProblem | null, field: string): string | undefined {
  if (problem?.status !== 400 || !problem.errors) return undefined
  const key = Object.keys(problem.errors).find((name) => name.toLowerCase().endsWith(field.toLowerCase()))
  return key ? problem.errors[key]?.join(' ') : undefined
}

type ProductLocation = NonNullable<RepairEventRequest['productLocation']>

const fields = [
  'reportedAt',
  'source',
  'note',
  'repairCentreId',
  'reason',
  'handedOverOn',
  'centreReference',
  'estimateAmount',
  'productLocation',
  'correctsSeq',
  'toStatus',
] as const

const pad = (value: number) => String(value).padStart(2, '0')

/** `datetime-local` value of a local time, minutes precision. */
const localDateTime = (date: Date) =>
  `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`

const localDate = (date: Date) => localDateTime(date).slice(0, 10)

/**
 * One dialog per repair event (ui-screens.md §2): reported date-time (default now, not in the future),
 * source, the event's own fields and an optional note. Submits with `If-Match` = the repair ETag.
 */
export function RepairEventDialog({ claimId, repair, type, onClose, onRecorded, onReload }: RepairEventDialogProps) {
  // The latest time the user acted: the bound for 'not in the future' (refreshed in event handlers).
  const [now, setNow] = useState(() => new Date())
  const [reportedAt, setReportedAt] = useState(() => localDateTime(now))
  const [source, setSource] = useState<ReportSource | ''>('')
  const [note, setNote] = useState('')
  const correcting = type === 'Correction'
  // A correction starts from the current values; a hand-over from today.
  const [handedOverOn, setHandedOverOn] = useState(() => (correcting && repair.handedOverOn) || localDate(now))
  const [centreReference, setCentreReference] = useState(() => (correcting && repair.centreReference) || '')
  const [correctsSeq, setCorrectsSeq] = useState('')
  const [correctField, setCorrectField] = useState<CorrectableField | ''>('')
  const [correctedStatus, setCorrectedStatus] = useState<RepairStatus | ''>('')
  const [repairCentreId, setRepairCentreId] = useState('')
  const [reason, setReason] = useState('')
  const [estimateAmount, setEstimateAmount] = useState('')
  const [productLocation, setProductLocation] = useState<ProductLocation | ''>('')
  const [problem, setProblem] = useState<EventProblem | null>(null)
  const currency = useMe().data?.tenantCurrency
  const [reloading, setReloading] = useState(false)
  const record = useRecordRepairEvent()
  const choosesCentre = type === 'CentreChanged' || type === 'Transferred'
  const centres = useRepairCentres(claimId, choosesCentre)

  const title = repairActionLabels[type]
  const reported = reportedAt ? new Date(reportedAt) : undefined
  const reportedValid = reported !== undefined && !Number.isNaN(reported.getTime()) && reported.getTime() <= now.getTime()
  const trimmedReason = reason.trim()
  const trimmedNote = note.trim()

  const reasonValid = trimmedReason.length >= repairReasonLimits.min && trimmedReason.length <= repairReasonLimits.max
  const estimate = estimateAmount.trim() === '' ? undefined : Number(estimateAmount)
  const estimateValid = estimate !== undefined && Number.isFinite(estimate) && estimate > 0
  const aboveLimit = estimateValid && isAboveEconomicLimit(estimate, repair.economicLimitAmount)

  let eventValid = true
  if (type === 'HandedOver') eventValid = handedOverOn !== '' && handedOverOn <= localDate(now)
  if (choosesCentre) eventValid = repairCentreId !== '' && reasonValid
  if (type === 'RepairFailed') eventValid = reasonValid
  if (type === 'EstimateReported') eventValid = estimateValid
  if (type === 'NewAttemptStarted') eventValid = productLocation !== ''

  const correctable = repair.events.filter((event) => correctableFields(event).length > 0).sort((a, b) => a.seq - b.seq)
  const corrected = correctable.find((event) => String(event.seq) === correctsSeq)
  const fieldOptions = corrected ? correctableFields(corrected) : []
  if (correcting) {
    const fieldValid =
      correctField === 'handedOverOn'
        ? handedOverOn !== '' && handedOverOn <= localDate(now)
        : correctField === 'centreReference'
          ? centreReference.trim() !== ''
          : correctField === 'estimateAmount'
            ? estimateValid
            : correctField === 'toStatus' && correctedStatus !== ''
    eventValid = corrected !== undefined && fieldOptions.includes(correctField as CorrectableField) && fieldValid && reasonValid
  }
  const valid = reportedValid && source !== '' && trimmedNote.length <= repairReasonLimits.max && eventValid

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
    const request: RepairEventRequest = { type, reportedAt: reported.toISOString(), source }
    if (trimmedNote) request.note = trimmedNote
    if (type === 'HandedOver') {
      request.handedOverOn = handedOverOn
      if (centreReference.trim()) request.centreReference = centreReference.trim()
    }
    if (choosesCentre) {
      request.repairCentreId = repairCentreId
      request.reason = trimmedReason
    }
    if (type === 'RepairFailed') request.reason = trimmedReason
    if (type === 'EstimateReported') request.estimateAmount = estimate
    if (type === 'NewAttemptStarted' && productLocation) request.productLocation = productLocation
    if (correcting && corrected) {
      request.correctsSeq = corrected.seq
      request.reason = trimmedReason
      if (correctField === 'handedOverOn') request.handedOverOn = handedOverOn
      if (correctField === 'centreReference') request.centreReference = centreReference.trim()
      if (correctField === 'estimateAmount') request.estimateAmount = estimate
      if (correctField === 'toStatus' && correctedStatus) request.toStatus = correctedStatus
    }
    setProblem(null)
    record.mutate(
      { claimId, etag: repair.etag, request },
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
        <span>{problem.detail || problem.title || 'This update is not allowed in the repair’s current status.'}</span>
        {reloadButton}
      </div>
    )
  } else if (problem?.status === 412) {
    error = (
      <div className={styles.problem}>
        <span>This repair changed since you opened it. Reload to see the latest status.</span>
        {reloadButton}
      </div>
    )
  } else if (problem && !Object.values(errors).some(Boolean)) {
    error = [problem.title, problem.detail].filter(Boolean).join(' — ') || 'The update could not be recorded.'
  }

  const otherCentres = (centres.data ?? []).filter((centre) => centre.id !== repair.activeCentre?.id)
  const money = (value: number) => (currency ? formatMoney(value, currency) : value.toFixed(2))

  const reasonField = (
    <TextArea
      label="Reason"
      hint="Internal — never shown to the claimant."
      required
      rows={3}
      characterLimits={repairReasonLimits}
      value={reason}
      error={errors.reason}
      onChange={(event) => {
        setReason(event.target.value)
        clearFieldError('reason')
      }}
    />
  )

  const handedOverOnField = (
    <TextInput
      type="date"
      label="Handed over on"
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
  )

  const centreReferenceField = (label: string, required: boolean) => (
    <TextInput
      label={label}
      hint="The repair centre’s own reference for this product."
      required={required}
      value={centreReference}
      error={errors.centreReference}
      onChange={(event) => {
        setCentreReference(event.target.value)
        clearFieldError('centreReference')
      }}
    />
  )

  const estimateField = (
    <TextInput
      type="number"
      label={currency ? `Estimate (${currency})` : 'Estimate'}
      hint={
        repair.economicLimitAmount === undefined
          ? 'The repair centre’s cost estimate. The policy has no economic limit.'
          : `The repair centre’s cost estimate. Economic limit: ${money(repair.economicLimitAmount)}.`
      }
      required
      min={0.01}
      step={0.01}
      inputMode="decimal"
      value={estimateAmount}
      error={errors.estimateAmount ?? (estimateAmount.trim() !== '' && !estimateValid ? 'Enter an amount above zero.' : undefined)}
      onChange={(event) => {
        setEstimateAmount(event.target.value)
        clearFieldError('estimateAmount')
      }}
    />
  )

  return (
    <Dialog
      open
      onClose={onClose}
      title={title}
      description="Record what the repair centre reported."
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
          hint="When the repair centre reported it; not in the future."
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

        {type === 'HandedOver' && (
          <>
            {handedOverOnField}
            {centreReferenceField('Centre reference (optional)', false)}
          </>
        )}

        {correcting && (
          <>
            <Select
              label="Entry to correct"
              required
              value={correctsSeq}
              error={errors.correctsSeq}
              onChange={(event) => {
                setCorrectsSeq(event.target.value)
                const next = correctable.find((entry) => String(entry.seq) === event.target.value)
                const nextFields = next ? correctableFields(next) : []
                setCorrectField(nextFields.length === 1 ? nextFields[0]! : '')
                clearFieldError('correctsSeq')
              }}
            >
              <option value="">{correctable.length === 0 ? 'No entry can be corrected' : 'Choose an entry'}</option>
              {correctable.map((entry) => (
                <option key={entry.seq} value={entry.seq}>
                  {repairEventName(entry)} · reported {formatDateTime(entry.reportedAt)}
                </option>
              ))}
            </Select>
            {fieldOptions.length > 1 && (
              <Select
                label="What to correct"
                required
                value={correctField}
                onChange={(event) => setCorrectField(event.target.value as CorrectableField | '')}
              >
                <option value="">Choose a field</option>
                {fieldOptions.map((field) => (
                  <option key={field} value={field}>
                    {correctableFieldLabels[field]}
                  </option>
                ))}
              </Select>
            )}
            {correctField === 'handedOverOn' && handedOverOnField}
            {correctField === 'centreReference' && centreReferenceField('Centre reference', true)}
            {correctField === 'estimateAmount' && estimateField}
            {correctField === 'toStatus' && corrected && (
              <Select
                label="Correct status"
                required
                value={correctedStatus}
                error={errors.toStatus}
                onChange={(event) => {
                  setCorrectedStatus(event.target.value as RepairStatus | '')
                  clearFieldError('toStatus')
                }}
              >
                <option value="">Choose a status</option>
                {repairStatuses
                  .filter((status) => status !== corrected.toStatus)
                  .map((status) => (
                    <option key={status} value={status}>
                      {repairStatusPresentation(status).label}
                    </option>
                  ))}
              </Select>
            )}
            {reasonField}
            <p className={styles.note}>The original entry stays in the history, shown struck through.</p>
          </>
        )}

        {choosesCentre && (
          <>
            <Select
              label="New repair centre"
              required
              value={repairCentreId}
              disabled={centres.isPending}
              error={errors.repairCentreId ?? (centres.isError ? 'The repair centres could not be loaded.' : undefined)}
              onChange={(event) => {
                setRepairCentreId(event.target.value)
                clearFieldError('repairCentreId')
              }}
            >
              <option value="">{centres.isPending ? 'Loading repair centres…' : 'Choose a repair centre'}</option>
              {otherCentres.map((centre) => (
                <option key={centre.id} value={centre.id}>
                  {centre.name}
                  {centre.suggested ? ' (suggested)' : ''}
                  {centre.servesClaim ? '' : ' — does not serve this claim'}
                </option>
              ))}
            </Select>
            {reasonField}
            {type === 'Transferred' && (
              <p className={styles.note}>
                The repair goes back to Awaiting hand-over at the new centre. Failed attempts stay {failedAttemptsText(repair)}.
              </p>
            )}
          </>
        )}

        {type === 'EstimateReported' && estimateField}
        {type === 'EstimateReported' && aboveLimit && (
          <p className={styles.note} role="status">
            Above the economic limit: the repair becomes Repair not possible and a reviewer decides the remedy.
          </p>
        )}

        {type === 'RepairFailed' && (
          <>
            {reasonField}
            <p className={styles.note}>
              Failed attempts so far: {failedAttemptsText(repair)}. The policy is evaluated again after this failure.
            </p>
          </>
        )}

        {type === 'NewAttemptStarted' && (
          <fieldset className={styles.choices} aria-describedby={errors.productLocation ? 'product-location-error' : undefined}>
            <legend className={styles.legend}>
              Where is the product?<span aria-hidden="true"> *</span>
            </legend>
            {(Object.keys(productLocationLabels) as ProductLocation[]).map((option) => (
              <label key={option} className={styles.choice}>
                <input
                  type="radio"
                  name="productLocation"
                  value={option}
                  checked={productLocation === option}
                  onChange={() => {
                    setProductLocation(option)
                    clearFieldError('productLocation')
                  }}
                />
                <span>{productLocationLabels[option]}</span>
              </label>
            ))}
            {errors.productLocation && (
              <p id="product-location-error" className={styles.fieldError}>
                {errors.productLocation}
              </p>
            )}
          </fieldset>
        )}

        <TextArea
          label="Note (optional)"
          hint="Internal — never shown to the claimant."
          rows={3}
          characterLimits={{ max: repairReasonLimits.max }}
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
