import { useState, type FormEvent, type MouseEvent, type ReactNode } from 'react'
import { useMe } from '../../app/staffQueries'
import { ApiProblem } from '../../shared/api/client'
import { formatDateTime, formatMoney, replacementStatusPresentation } from '../../shared/presentation'
import { Button, Dialog, Select, TextArea, TextInput } from '../../shared/ui'
import {
  useRecordReplacementEvent,
  useReplacementLocations,
  useReplacementModels,
  type ReplacementEventRequest,
  type ReplacementView,
  type ReportSource,
} from './remedyQueries'
import { reportSourceLabel, reportSources } from './remedyView'
import {
  needsReturnConfirmation,
  replacementActionLabels,
  replacementCorrectableFieldLabels,
  replacementCorrectableFields,
  replacementEventName,
  replacementModelText,
  replacementReasonLimits,
  type ReplacementCorrectableField,
  type ReplacementDialogEvent,
} from './replacementView'
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
  /** The claim's serial: a recorded return is compared with it, and a reviewer accepting a mismatch sees both. */
  claimedSerial?: string
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

const fields = [
  'reportedAt',
  'source',
  'note',
  'locationId',
  'productId',
  'reason',
  'returnedOn',
  'serialSeen',
  'serialNumber',
  'handedOverOn',
  'returnConfirmed',
  'correctsSeq',
  'correction',
  'toStatus',
] as const

const pad = (value: number) => String(value).padStart(2, '0')

/** `datetime-local` value of a local time, minutes precision. */
const localDateTime = (date: Date) =>
  `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`

const localDate = (date: Date) => localDateTime(date).slice(0, 10)

const descriptions: Record<ReplacementDialogEvent, string> = {
  LocationChanged: 'Choose another location to issue the replacement.',
  ReturnRecorded: 'Record the faulty product the customer returned to the location.',
  ModelProposed: 'Propose another model when the claimed one cannot be supplied. A claims reviewer must approve it.',
  ModelApproved: 'Approve the proposed model: a unit of it can then be reserved for the customer.',
  ModelDeclined: 'Decline the proposed model: the claimed model remains.',
  ReturnAccepted: 'Accept the returned product although its serial does not match the claim’s.',
  UnitReserved: 'Record the unit the location reserved for the customer.',
  UnitRefused: 'Record that the customer refused the unit at collection, for example damaged, the wrong model or not working.',
  HandedToCustomer: 'Record that the customer collected the replacement.',
  NotAvailable: 'Record that no replacement unit can be supplied.',
  Moved: 'Move the replacement to another location.',
  Correction: 'Correct an entry recorded by mistake. The original entry stays in the history.',
}

/** Events whose request carries a reason of 10–1000 characters (003 contracts, `ReplacementEventRequest`). */
const reasonEvents: readonly ReplacementDialogEvent[] = [
  'LocationChanged',
  'ReturnAccepted',
  'ModelProposed',
  'ModelDeclined',
  'UnitRefused',
  'NotAvailable',
  'Moved',
  'Correction',
]

/** The pending model proposal a reviewer decides: the model, the reason, who proposed it and when. */
function PendingProposal({ proposal }: { proposal: NonNullable<ReplacementView['pendingProposal']> }) {
  return (
    <dl className={styles.facts}>
      <div>
        <dt>Proposed model</dt>
        <dd>{replacementModelText(proposal.model)}</dd>
      </div>
      <div>
        <dt>Reason</dt>
        <dd>{proposal.reason}</dd>
      </div>
      <div>
        <dt>Proposed by</dt>
        <dd>
          {proposal.proposedBy} · {formatDateTime(proposal.proposedAt)}
        </dd>
      </div>
    </dl>
  )
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
  claimedSerial,
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
  const [productId, setProductId] = useState('')
  const [reason, setReason] = useState('')
  const [serialNumber, setSerialNumber] = useState('')
  const correcting = type === 'Correction'
  // A correction of the return date starts from the recorded one; a return from today.
  const [returnedOn, setReturnedOn] = useState(() => (correcting && replacement.faultyProductReturn?.returnedOn) || localDate(now))
  const [serialSeen, setSerialSeen] = useState('')
  const [handedOverOn, setHandedOverOn] = useState(() => localDate(now))
  const [returnConfirmed, setReturnConfirmed] = useState(false)
  const [correctsSeq, setCorrectsSeq] = useState('')
  const [correctField, setCorrectField] = useState<ReplacementCorrectableField | ''>('')
  const [problem, setProblem] = useState<EventProblem | null>(null)
  const [reloading, setReloading] = useState(false)
  const record = useRecordReplacementEvent()
  const choosesLocation = type === 'LocationChanged' || type === 'Moved'
  const proposesModel = type === 'ModelProposed'
  const decidesModel = type === 'ModelApproved' || type === 'ModelDeclined'
  const needsReason = reasonEvents.includes(type)
  const locations = useReplacementLocations(claimId, choosesLocation)
  // Only the models the policy allows are offered (GET /replacement-models); none under "same model only".
  const models = useReplacementModels(claimId, proposesModel)
  const currency = useMe().data?.tenantCurrency
  const confirmsReturn = type === 'HandedToCustomer' && needsReturnConfirmation(replacement)
  const unitModel: UnitModel = replacement.approvedModel ?? claimedModel

  const title = replacementActionLabels[type]
  const reported = reportedAt ? new Date(reportedAt) : undefined
  const reportedValid = reported !== undefined && !Number.isNaN(reported.getTime()) && reported.getTime() <= now.getTime()
  const trimmedReason = reason.trim()
  const trimmedNote = note.trim()
  const trimmedSerial = serialNumber.trim()
  const trimmedSerialSeen = serialSeen.trim()
  const reasonValid = trimmedReason.length >= replacementReasonLimits.min && trimmedReason.length <= replacementReasonLimits.max

  let eventValid = true
  if (choosesLocation) eventValid = locationId !== '' && reasonValid
  if (type === 'ReturnRecorded') eventValid = returnedOn !== '' && returnedOn <= localDate(now) && trimmedSerialSeen !== ''
  if (type === 'ReturnAccepted' || type === 'ModelDeclined' || type === 'NotAvailable' || type === 'UnitRefused') eventValid = reasonValid
  if (proposesModel) eventValid = productId !== '' && reasonValid
  if (decidesModel && !replacement.pendingProposal) eventValid = false
  if (type === 'UnitReserved') eventValid = trimmedSerial !== ''
  if (type === 'HandedToCustomer') eventValid = handedOverOn !== '' && handedOverOn <= localDate(now) && (!confirmsReturn || returnConfirmed)

  const correctable = replacement.events
    .filter((entry) => replacementCorrectableFields(entry, replacement).length > 0)
    .sort((a, b) => a.seq - b.seq)
  const corrected = correctable.find((entry) => String(entry.seq) === correctsSeq)
  const fieldOptions = corrected ? replacementCorrectableFields(corrected, replacement) : []
  // A status correction sends the entry back to the status before it.
  const sentBackTo = correctField === 'toStatus' ? corrected?.fromStatus : undefined
  if (correcting) {
    const fieldValid =
      correctField === 'returnedOn'
        ? returnedOn !== '' && returnedOn <= localDate(now)
        : correctField === 'serialSeen'
          ? trimmedSerialSeen !== ''
          : correctField === 'serialNumber'
            ? trimmedSerial !== ''
            : correctField === 'toStatus' && sentBackTo !== undefined
    eventValid = corrected !== undefined && fieldOptions.includes(correctField as ReplacementCorrectableField) && fieldValid && reasonValid
  }
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
    if (choosesLocation) request.locationId = locationId
    if (proposesModel) request.productId = productId
    if (needsReason) request.reason = trimmedReason
    if (type === 'ReturnRecorded') {
      request.returnedOn = returnedOn
      request.serialSeen = trimmedSerialSeen
    }
    if (type === 'UnitReserved') request.serialNumber = trimmedSerial
    if (type === 'HandedToCustomer') {
      request.handedOverOn = handedOverOn
      if (confirmsReturn) request.returnConfirmed = true
    }
    if (correcting && corrected) {
      request.correctsSeq = corrected.seq
      if (correctField === 'returnedOn') request.returnedOn = returnedOn
      if (correctField === 'serialSeen') request.serialSeen = trimmedSerialSeen
      if (correctField === 'serialNumber') request.serialNumber = trimmedSerial
      if (correctField === 'toStatus' && sentBackTo) request.toStatus = sentBackTo
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
  const proposable = models.data ?? []
  const money = (value: number) => (currency ? formatMoney(value, currency) : value.toFixed(2))

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
            {type === 'Moved' && (
              <p className={styles.note}>
                {replacement.currentUnit?.result === 'Reserved'
                  ? 'The unit reserved at the current location is released and the replacement awaits a new unit at the new location. The customer is notified again once a unit is reserved there.'
                  : 'The current location stays in the history. The remedy does not change.'}
              </p>
            )}
          </>
        )}

        {type === 'UnitRefused' && replacement.currentUnit && (
          <>
            <dl className={styles.facts}>
              <div>
                <dt>Refused unit</dt>
                <dd>
                  <span className={styles.mono}>{replacement.currentUnit.modelCode}</span> ·{' '}
                  <span className={styles.mono}>{replacement.currentUnit.serialNumber}</span>
                </dd>
              </div>
            </dl>
            <p className={styles.note}>
              The unit stays in the history and the replacement awaits a new unit. The remedy does not change.
            </p>
          </>
        )}

        {correcting && (
          <>
            <Select
              label="Entry to correct"
              required
              value={correctsSeq}
              error={errors.correctsSeq ?? errors.correction}
              onChange={(event) => {
                setCorrectsSeq(event.target.value)
                const next = correctable.find((entry) => String(entry.seq) === event.target.value)
                const nextFields = next ? replacementCorrectableFields(next, replacement) : []
                setCorrectField(nextFields.length === 1 ? nextFields[0]! : '')
                clearFieldError('correctsSeq')
                clearFieldError('correction')
              }}
            >
              <option value="">{correctable.length === 0 ? 'No entry can be corrected' : 'Choose an entry'}</option>
              {correctable.map((entry) => (
                <option key={entry.seq} value={entry.seq}>
                  {replacementEventName(entry)} · reported {formatDateTime(entry.reportedAt)}
                </option>
              ))}
            </Select>
            {fieldOptions.length > 1 && (
              <Select
                label="What to correct"
                required
                value={correctField}
                onChange={(event) => setCorrectField(event.target.value as ReplacementCorrectableField | '')}
              >
                <option value="">Choose what to correct</option>
                {fieldOptions.map((field) => (
                  <option key={field} value={field}>
                    {replacementCorrectableFieldLabels[field]}
                  </option>
                ))}
              </Select>
            )}
            {correctField === 'returnedOn' && (
              <TextInput
                type="date"
                label="Correct return date"
                required
                max={localDate(now)}
                value={returnedOn}
                error={errors.returnedOn}
                onChange={(event) => {
                  setReturnedOn(event.target.value)
                  setNow(new Date())
                  clearFieldError('returnedOn')
                }}
              />
            )}
            {correctField === 'serialSeen' && (
              <>
                <TextInput
                  label="Correct serial seen on the returned product"
                  required
                  value={serialSeen}
                  error={errors.serialSeen}
                  onChange={(event) => {
                    setSerialSeen(event.target.value)
                    clearFieldError('serialSeen')
                  }}
                />
                <p className={styles.note}>
                  The corrected serial is compared with the claim’s again. If it does not match, a claims reviewer must accept the
                  return.
                </p>
              </>
            )}
            {correctField === 'serialNumber' && (
              <TextInput
                label="Correct serial number of the unit"
                hint="It must differ from the claimed product’s serial."
                required
                value={serialNumber}
                error={errors.serialNumber}
                onChange={(event) => {
                  setSerialNumber(event.target.value)
                  clearFieldError('serialNumber')
                }}
              />
            )}
            {correctField === 'toStatus' && corrected && sentBackTo && (
              <p className={styles.note}>
                {corrected.type === 'UnitReserved'
                  ? `The replacement goes back to ${replacementStatusPresentation(sentBackTo).label}. The notification already sent to the customer is not withdrawn; reserving the unit again sends no second one.`
                  : `The replacement goes back to ${replacementStatusPresentation(sentBackTo).label} with the refused unit reserved again.`}
                {errors.toStatus && <span className={styles.fieldError}> {errors.toStatus}</span>}
              </p>
            )}
            <p className={styles.note}>The original entry stays in the history, shown struck through.</p>
          </>
        )}

        {type === 'ReturnRecorded' && (
          <>
            <TextInput
              type="date"
              label="Returned on"
              required
              max={localDate(now)}
              value={returnedOn}
              error={errors.returnedOn}
              onChange={(event) => {
                setReturnedOn(event.target.value)
                setNow(new Date())
                clearFieldError('returnedOn')
              }}
            />
            <TextInput
              label="Serial number on the returned product"
              hint="As printed on the product."
              required
              value={serialSeen}
              error={errors.serialSeen}
              onChange={(event) => {
                setSerialSeen(event.target.value)
                clearFieldError('serialSeen')
              }}
            />
            <p className={styles.note}>
              The serial is compared with the claim’s. If it does not match, a claims reviewer must accept the return before
              the replacement can go on.
            </p>
          </>
        )}

        {type === 'ReturnAccepted' && replacement.faultyProductReturn && (
          <dl className={styles.facts}>
            <div>
              <dt>Serial seen</dt>
              <dd className={styles.mono}>{replacement.faultyProductReturn.serialSeen ?? '—'}</dd>
            </div>
            <div>
              <dt>Claim serial</dt>
              <dd className={styles.mono}>{claimedSerial ?? '—'}</dd>
            </div>
          </dl>
        )}

        {proposesModel && (
          <>
            <p className={styles.note}>Claimed model: {replacementModelText(claimedModel)}</p>
            {models.isSuccess && proposable.length === 0 ? (
              <p className={styles.note}>The policy allows no other model for this claim: only the claimed model can be handed out.</p>
            ) : (
              <Select
                label="Model"
                hint="Only models the policy allows: the same category with at least the claimed model’s claim value."
                required
                value={productId}
                disabled={models.isPending}
                error={errors.productId ?? (models.isError ? 'The models could not be loaded.' : undefined)}
                onChange={(event) => {
                  setProductId(event.target.value)
                  clearFieldError('productId')
                }}
              >
                <option value="">{models.isPending ? 'Loading models…' : 'Choose a model'}</option>
                {proposable.map((model) => (
                  <option key={model.productId} value={model.productId}>
                    {replacementModelText(model)} · claim value {money(model.claimValue)}
                  </option>
                ))}
              </Select>
            )}
          </>
        )}

        {decidesModel &&
          (replacement.pendingProposal ? (
            <PendingProposal proposal={replacement.pendingProposal} />
          ) : (
            <p className={styles.note}>No model proposal is waiting for a decision. Reload to see the latest status.</p>
          ))}

        {needsReason && (
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
        )}

        {type === 'UnitReserved' && (
          <>
            <p className={styles.note}>
              Model: {replacementModelText(unitModel)}
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

        {type === 'ModelProposed' && (
          <p className={styles.note}>No unit can be reserved for the proposed model until a reviewer approves it.</p>
        )}

        {type === 'NotAvailable' && (
          <p className={styles.note}>
            This ends the replacement. The policy is evaluated again and a claims reviewer confirms the remedy; the customer sees
            that the claim is being reviewed.
          </p>
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
