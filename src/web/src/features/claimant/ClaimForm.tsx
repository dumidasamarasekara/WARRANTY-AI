import { useMutation } from '@tanstack/react-query'
import { useEffect, useId, useRef, useState, type ChangeEvent, type FormEvent, type HTMLAttributes } from 'react'
import { ApiProblem } from '../../shared/api/client'
import { Alert, Button, FileDrop, Stepper, TextArea, TextInput, type FileDropItem, type StepperStep } from '../../shared/ui'
import {
  acceptedFiles,
  buildClaimFormData,
  checkFile,
  claimantFormSteps,
  descriptionLimits,
  emptyClaimFormValues,
  fieldForProblemKey,
  fileHint,
  localToday,
  maxPhotos,
  stepOf,
  toSubmissionData,
  validateDetails,
  validateEvidence,
  type ClaimContact,
  type ClaimFormErrors,
  type ClaimFormStep,
  type FormField,
  type SubmissionAccepted,
  type TextField,
} from './claimSubmission'
import styles from './ClaimForm.module.css'

export type ClaimFormVariant = 'claimant' | 'staff'

export interface ClaimFormProps {
  /** `claimant`: portal copy, 40 px controls, four-step claimant stepper; `staff`: agent copy, 36 px controls. */
  variant: ClaimFormVariant
  /** Posts the multipart `ClaimSubmissionForm`; rejects with an {@link ApiProblem} on failure. */
  submit: (body: FormData) => Promise<SubmissionAccepted>
  onSubmitted: (accepted: SubmissionAccepted, contact: ClaimContact) => void
}

const copy = {
  claimant: {
    step1: 'Your details',
    lead1: 'Tell us who you are and about the product you bought.',
    step2: 'What went wrong and your files',
    lead2: 'Clear photos help us decide faster. Add your invoice and 1–8 photos of the product.',
    customer: 'About you',
    product: 'Your product',
    purchase: 'Your purchase',
    description: 'What went wrong?',
  },
  staff: {
    step1: 'Customer and product',
    lead1: "Enter the customer's details as they gave them.",
    step2: 'Problem and files',
    lead2: "Add the customer's invoice and 1–8 photos of the product.",
    customer: 'Customer',
    product: 'Product',
    purchase: 'Purchase',
    description: 'Problem description',
  },
} as const

const staffSteps = (step: ClaimFormStep): StepperStep[] => [
  { key: 'details', label: 'Details', state: step === 1 ? 'current' : 'done' },
  { key: 'evidence', label: 'Evidence', state: step === 2 ? 'current' : 'upcoming' },
]

interface PickedFile {
  key: string
  file: File
}

let nextFileKey = 0
const pick = (file: File): PickedFile => ({ key: `file-${++nextFileKey}`, file })
const toItem = ({ key, file }: PickedFile): FileDropItem => ({ key, name: file.name, size: file.size, status: 'done' })

function withError(errors: ClaimFormErrors, field: FormField, message: string | undefined): ClaimFormErrors {
  const next = { ...errors }
  if (message) next[field] = message
  else delete next[field]
  return next
}

/** Field errors of a ValidationProblemDetails that the form can attach to a field. */
function fieldErrorsOf(error: unknown): ClaimFormErrors {
  const errors: ClaimFormErrors = {}
  if (!(error instanceof ApiProblem)) return errors
  for (const [key, messages] of Object.entries(error.errors)) {
    const field = fieldForProblemKey(key)
    if (field && messages[0] && !errors[field]) errors[field] = messages[0]
  }
  return errors
}

/** A failed submission: ProblemDetails title and detail, unattached field errors, correlation ID (ui-design.md §8). */
function SubmissionProblem({ error }: { error: unknown }) {
  if (!(error instanceof ApiProblem)) {
    return (
      <Alert tone="err" urgent title="We couldn't send the claim">
        Check your connection and try again.
      </Alert>
    )
  }
  const unattached = Object.entries(error.errors)
    .filter(([key]) => !fieldForProblemKey(key))
    .flatMap(([, messages]) => messages)
  const hasFieldErrors = Object.keys(error.errors).length > unattached.length
  return (
    <Alert tone="err" urgent title={error.title ?? "We couldn't submit the claim"}>
      {error.detail && <p>{error.detail}</p>}
      {hasFieldErrors && <p>Check the highlighted fields.</p>}
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

interface TextOptions {
  required?: boolean
  type?: 'text' | 'email' | 'tel' | 'date'
  autoComplete?: string
  hint?: string
  wide?: boolean
  maxLength?: number
  max?: string
  inputMode?: HTMLAttributes<HTMLInputElement>['inputMode']
  upperCase?: boolean
}

/**
 * The claim submission form (FR-007): step 1 customer, product and purchase; step 2 the problem
 * description, one invoice and 1–8 photos. Validates like the API (FR-009) before sending and attaches
 * the API's field errors to their fields. Used by the claimant portal and the staff "new claim" page.
 */
export function ClaimForm({ variant, submit, onSubmitted }: ClaimFormProps) {
  const baseId = useId()
  const text = copy[variant]
  const size = variant === 'claimant' ? 'lg' : 'md'
  const [step, setStep] = useState<ClaimFormStep>(1)
  const [values, setValues] = useState(emptyClaimFormValues)
  const [errors, setErrors] = useState<ClaimFormErrors>({})
  const [invoice, setInvoice] = useState<PickedFile | null>(null)
  const [photos, setPhotos] = useState<PickedFile[]>([])
  const [focusRequest, setFocusRequest] = useState<{ target: 'heading' | 'invalid'; id: number } | null>(null)
  const formRef = useRef<HTMLFormElement>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    if (!focusRequest) return
    if (focusRequest.target === 'heading') headingRef.current?.focus()
    else formRef.current?.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus()
  }, [focusRequest])

  // A new request object each time, so the effect runs even when the target repeats.
  const requestFocus = (target: 'heading' | 'invalid') =>
    setFocusRequest((previous) => ({ target, id: (previous?.id ?? 0) + 1 }))

  const submission = useMutation({
    mutationFn: ({ body }: { body: FormData; contact: ClaimContact }) => submit(body),
    onSuccess: (accepted, { contact }) => onSubmitted(accepted, contact),
    onError: (error) => {
      const fieldErrors = fieldErrorsOf(error)
      const fields = Object.keys(fieldErrors) as FormField[]
      if (fields.length === 0) return
      setErrors(fieldErrors)
      setStep(fields.some((field) => stepOf(field) === 1) ? 1 : 2)
      requestFocus('invalid')
    },
  })

  const change = (field: TextField, upperCase = false) => (event: ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => {
    const value = upperCase ? event.target.value.toUpperCase() : event.target.value
    setValues((current) => ({ ...current, [field]: value }))
    setErrors((current) => withError(current, field, undefined))
  }

  const textInput = (field: TextField, label: string, options: TextOptions = {}) => (
    <TextInput
      id={`${baseId}-${field.replace('.', '-')}`}
      label={label}
      size={size}
      type={options.type ?? 'text'}
      required={options.required}
      hint={options.hint}
      maxLength={options.maxLength}
      max={options.max}
      inputMode={options.inputMode}
      autoComplete={variant === 'claimant' ? (options.autoComplete ?? 'off') : 'off'}
      className={options.wide ? styles.wide : undefined}
      value={values[field]}
      error={errors[field]}
      onChange={change(field, options.upperCase)}
    />
  )

  function pickInvoice(files: File[]) {
    const file = files[0]
    if (!file) return
    const problem = checkFile(file)
    if (!problem) setInvoice(pick(file))
    setErrors((current) => withError(current, 'invoice', problem))
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
    setErrors((current) => withError(current, 'photos', problem))
  }

  function goTo(next: ClaimFormStep) {
    setStep(next)
    requestFocus('heading')
  }

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (submission.isPending) return
    if (step === 1) {
      const detailErrors = validateDetails(values, localToday())
      setErrors(detailErrors)
      if (Object.keys(detailErrors).length > 0) requestFocus('invalid')
      else goTo(2)
      return
    }
    const evidenceErrors = validateEvidence(values, invoice?.file ?? null, photos.map((photo) => photo.file))
    const allErrors = { ...validateDetails(values, localToday()), ...evidenceErrors }
    setErrors(allErrors)
    const fields = Object.keys(allErrors) as FormField[]
    if (fields.length > 0 || !invoice) {
      if (fields.some((field) => stepOf(field) === 1)) setStep(1)
      requestFocus('invalid')
      return
    }
    const claim = toSubmissionData(values)
    submission.mutate({
      body: buildClaimFormData(claim, invoice.file, photos.map((photo) => photo.file)),
      contact: { email: claim.customer.email, phone: claim.customer.phone },
    })
  }

  return (
    <div className={styles.claimForm}>
      <Stepper
        label="Claim progress"
        steps={variant === 'claimant' ? claimantFormSteps(step) : staffSteps(step)}
        className={styles.stepper}
      />
      <form ref={formRef} className={styles.form} onSubmit={handleSubmit} noValidate>
        <div className={styles.intro}>
          <h2 ref={headingRef} tabIndex={-1} className={styles.stepTitle}>
            {step === 1 ? text.step1 : text.step2}
          </h2>
          <p className={styles.lead}>{step === 1 ? text.lead1 : text.lead2}</p>
        </div>

        {step === 1 ? (
          <>
            <fieldset className={styles.group}>
              <legend className={styles.legend}>{text.customer}</legend>
              <div className={styles.grid}>
                {textInput('customer.fullName', 'Full name', { required: true, autoComplete: 'name', wide: true })}
                {textInput('customer.email', 'Email address', { required: true, type: 'email', autoComplete: 'email' })}
                {textInput('customer.phone', 'Phone number', { type: 'tel', autoComplete: 'tel', hint: 'Optional' })}
                {textInput('customer.addressLine', 'Address', { autoComplete: 'street-address', hint: 'Optional', wide: true })}
                {textInput('customer.city', 'City', { autoComplete: 'address-level2', hint: 'Optional' })}
                {textInput('customer.postalCode', 'Postal code', { autoComplete: 'postal-code', hint: 'Optional' })}
                {textInput('customer.country', 'Country', {
                  required: true,
                  autoComplete: 'country',
                  hint: 'Two-letter code, e.g. US',
                  maxLength: 2,
                  upperCase: true,
                })}
              </div>
            </fieldset>

            <fieldset className={styles.group}>
              <legend className={styles.legend}>{text.product}</legend>
              <div className={styles.grid}>
                {textInput('product.modelCode', 'Model code', { required: true, hint: 'Printed on the product label' })}
                {textInput('product.serialNumber', 'Serial number', { required: true, hint: 'Printed on the product label' })}
              </div>
            </fieldset>

            <fieldset className={styles.group}>
              <legend className={styles.legend}>{text.purchase}</legend>
              <div className={styles.grid}>
                {textInput('purchase.date', 'Purchase date', { required: true, type: 'date', max: localToday() })}
                {textInput('purchase.place', 'Place of purchase', { required: true, hint: 'Shop or website' })}
                {textInput('purchase.price', 'Price paid', { required: true, inputMode: 'decimal', hint: 'e.g. 499.99' })}
                {textInput('purchase.currency', 'Currency', {
                  required: true,
                  hint: 'Three-letter code, e.g. USD',
                  maxLength: 3,
                  upperCase: true,
                })}
                {textInput('purchase.country', 'Country of purchase', {
                  required: true,
                  hint: 'Two-letter code, e.g. US',
                  maxLength: 2,
                  upperCase: true,
                })}
              </div>
            </fieldset>
          </>
        ) : (
          <div className={styles.evidence}>
            <TextArea
              id={`${baseId}-problemDescription`}
              label={text.description}
              hint="Say what happened and when the problem started."
              size={size}
              rows={6}
              required
              characterLimits={descriptionLimits}
              value={values.problemDescription}
              error={errors.problemDescription}
              onChange={change('problemDescription')}
            />
            <FileDrop
              label="Invoice"
              hint={fileHint}
              accept={acceptedFiles}
              files={invoice ? [toItem(invoice)] : []}
              error={errors.invoice}
              disabled={submission.isPending}
              onFiles={pickInvoice}
              onRemove={() => setInvoice(null)}
            />
            <FileDrop
              label={`Photos of the product (1–${maxPhotos})`}
              hint={fileHint}
              accept={acceptedFiles}
              multiple
              files={photos.map(toItem)}
              error={errors.photos}
              disabled={submission.isPending}
              onFiles={addPhotos}
              onRemove={(key) => setPhotos(photos.filter((photo) => photo.key !== key))}
            />
            {variant === 'claimant' && (
              <p className={styles.note}>Your documents are stored securely and used only to process this claim.</p>
            )}
          </div>
        )}

        {submission.isError && <SubmissionProblem error={submission.error} />}

        <div className={styles.actions}>
          {step === 2 && (
            <Button size={size} className={styles.action} disabled={submission.isPending} onClick={() => goTo(1)}>
              Back
            </Button>
          )}
          <Button type="submit" variant="primary" size={size} className={styles.action} loading={submission.isPending}>
            {step === 1 ? 'Continue' : 'Submit claim'}
          </Button>
        </div>
      </form>
    </div>
  )
}
