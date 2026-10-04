import type { Schemas } from '../../shared/api/client'
import type { StepperStep } from '../../shared/ui'

export type ClaimSubmissionData = Schemas['ClaimSubmissionData']
export type SubmissionAccepted = Schemas['SubmissionAccepted']

/** The text fields of the claim form, keyed by their path in `ClaimSubmissionData`. */
export const detailFields = [
  'customer.fullName',
  'customer.email',
  'customer.phone',
  'customer.addressLine',
  'customer.city',
  'customer.postalCode',
  'customer.country',
  'product.modelCode',
  'product.serialNumber',
  'purchase.date',
  'purchase.place',
  'purchase.price',
  'purchase.currency',
  'purchase.country',
] as const

export type DetailField = (typeof detailFields)[number]
export type TextField = DetailField | 'problemDescription'
export type FormField = TextField | 'invoice' | 'photos'

export type ClaimFormValues = Record<TextField, string>
export type ClaimFormErrors = Partial<Record<FormField, string>>

/** The contact details the claimant later uses, with the reference, to see the claim (FR-037a). */
export interface ClaimContact {
  email: string
  phone?: string
}

export const emptyClaimFormValues: ClaimFormValues = {
  'customer.fullName': '',
  'customer.email': '',
  'customer.phone': '',
  'customer.addressLine': '',
  'customer.city': '',
  'customer.postalCode': '',
  'customer.country': '',
  'product.modelCode': '',
  'product.serialNumber': '',
  'purchase.date': '',
  'purchase.place': '',
  'purchase.price': '',
  'purchase.currency': '',
  'purchase.country': '',
  problemDescription: '',
}

/** The form's two steps: the details, then the problem and the files. */
export type ClaimFormStep = 1 | 2

export function stepOf(field: FormField): ClaimFormStep {
  return (detailFields as readonly string[]).includes(field) ? 1 : 2
}

// Limits mirror ClaimSubmissionData and the upload rules in contracts/rest-api.openapi.yaml and research.md.
export const descriptionLimits = { min: 20, max: 4000 } as const
export const maxPhotos = 8
export const maxFileBytes = 15 * 1024 * 1024
export const fileHint = 'PDF, JPG, PNG or WebP · up to 15 MB each'
export const acceptedFiles = 'application/pdf,image/jpeg,image/png,image/webp,.pdf,.jpg,.jpeg,.png,.webp'

const maxLengths: Partial<Record<TextField, number>> = {
  'customer.fullName': 200,
  'customer.phone': 30,
  'customer.addressLine': 200,
  'customer.city': 100,
  'customer.postalCode': 20,
  'product.modelCode': 50,
  'product.serialNumber': 50,
  'purchase.place': 200,
}

const requiredMessages: Partial<Record<TextField, string>> = {
  'customer.fullName': 'Enter the full name',
  'customer.email': 'Enter an email address',
  'customer.country': 'Enter the country',
  'product.modelCode': 'Enter the model code',
  'product.serialNumber': 'Enter the serial number',
  'purchase.date': 'Enter the purchase date',
  'purchase.place': 'Enter where the product was bought',
  'purchase.price': 'Enter the price paid',
  'purchase.currency': 'Enter the currency',
  'purchase.country': 'Enter the country of purchase',
}

const emailPattern = /^[^\s@]+@[^\s@]+\.[^\s@]+$/
const countryPattern = /^[A-Z]{2}$/
const currencyPattern = /^[A-Z]{3}$/
const isoDatePattern = /^\d{4}-\d{2}-\d{2}$/
const pricePattern = /^\d+(\.\d+)?$/

const pad = (value: number) => String(value).padStart(2, '0')

/** Today's calendar date (`2026-10-04`) in the user's time zone: the claim date of a new claim. */
export function localToday(now: Date = new Date()): string {
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`
}

function isRealDate(value: string): boolean {
  if (!isoDatePattern.test(value)) return false
  const [year = 0, month = 0, day = 0] = value.split('-').map(Number)
  const date = new Date(year, month - 1, day)
  return date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day
}

/** Step 1: customer, product and purchase (FR-007, FR-009). `today` is the claim date. */
export function validateDetails(values: ClaimFormValues, today: string = localToday()): ClaimFormErrors {
  const errors: ClaimFormErrors = {}
  for (const field of detailFields) {
    const value = values[field].trim()
    const required = requiredMessages[field]
    if (!value) {
      if (required) errors[field] = required
      continue
    }
    const max = maxLengths[field]
    if (max !== undefined && value.length > max) errors[field] = `Use at most ${max} characters`
  }

  const email = values['customer.email'].trim()
  if (email && !errors['customer.email'] && !emailPattern.test(email)) {
    errors['customer.email'] = 'Enter an email address like name@example.com'
  }
  for (const field of ['customer.country', 'purchase.country'] as const) {
    const value = values[field].trim()
    if (value && !countryPattern.test(value.toUpperCase())) errors[field] = 'Use the two-letter country code, e.g. US'
  }
  const currency = values['purchase.currency'].trim()
  if (currency && !currencyPattern.test(currency.toUpperCase())) {
    errors['purchase.currency'] = 'Use the three-letter currency code, e.g. USD'
  }
  const price = values['purchase.price'].trim()
  if (price && (!pricePattern.test(price) || !(Number(price) > 0))) {
    errors['purchase.price'] = 'Enter the price as a number greater than 0, e.g. 499.99'
  }
  const date = values['purchase.date'].trim()
  if (date) {
    if (!isRealDate(date)) errors['purchase.date'] = 'Enter a valid date'
    else if (date > today) errors['purchase.date'] = "The purchase date can't be in the future or after the claim date"
  }
  return errors
}

const allowedTypes = new Set(['application/pdf', 'image/jpeg', 'image/png', 'image/webp'])
const allowedExtension = /\.(pdf|jpe?g|png|webp)$/i
const heicType = /^image\/hei[cf](-sequence)?$/i
const heicExtension = /\.hei[cf]$/i

/**
 * A quick check before upload; the API judges the file content (FR-009), so this only spares the
 * user a round trip. `undefined` when the file may be sent.
 */
export function checkFile(file: File): string | undefined {
  if (heicType.test(file.type) || heicExtension.test(file.name)) return 'Please send photos as JPEG or PNG'
  const typeAllowed = file.type ? allowedTypes.has(file.type) : allowedExtension.test(file.name)
  if (!typeAllowed) return `${file.name} can't be used: send a PDF, JPG, PNG or WebP file`
  if (file.size > maxFileBytes) return `${file.name} is larger than 15 MB`
  if (file.size === 0) return `${file.name} is empty`
  return undefined
}

/** Step 2: the description, one invoice and 1–8 photos. */
export function validateEvidence(values: ClaimFormValues, invoice: File | null, photos: readonly File[]): ClaimFormErrors {
  const errors: ClaimFormErrors = {}
  const length = values.problemDescription.trim().length
  if (length === 0) errors.problemDescription = 'Describe what went wrong'
  else if (length < descriptionLimits.min) {
    errors.problemDescription = `Describe the problem in at least ${descriptionLimits.min} characters`
  } else if (length > descriptionLimits.max) {
    errors.problemDescription = `Use at most ${descriptionLimits.max.toLocaleString('en-US')} characters`
  }
  if (!invoice) errors.invoice = 'Add the invoice'
  if (photos.length === 0) errors.photos = 'Add at least one photo of the product'
  else if (photos.length > maxPhotos) errors.photos = `Add at most ${maxPhotos} photos`
  return errors
}

const optional = (value: string) => value.trim() || undefined

/** The `claim` part: trimmed values, codes in upper case, optional fields left out when empty. */
export function toSubmissionData(values: ClaimFormValues): ClaimSubmissionData {
  return {
    customer: {
      fullName: values['customer.fullName'].trim(),
      email: values['customer.email'].trim(),
      phone: optional(values['customer.phone']),
      addressLine: optional(values['customer.addressLine']),
      city: optional(values['customer.city']),
      postalCode: optional(values['customer.postalCode']),
      country: values['customer.country'].trim().toUpperCase(),
    },
    product: {
      modelCode: values['product.modelCode'].trim(),
      serialNumber: values['product.serialNumber'].trim(),
    },
    purchase: {
      date: values['purchase.date'].trim(),
      place: values['purchase.place'].trim(),
      price: Number(values['purchase.price'].trim()),
      currency: values['purchase.currency'].trim().toUpperCase(),
      country: values['purchase.country'].trim().toUpperCase(),
    },
    problemDescription: values.problemDescription.trim(),
  }
}

/** `ClaimSubmissionForm`: part `claim` as JSON, one `invoice` part and one `photos` part per photo. */
export function buildClaimFormData(claim: ClaimSubmissionData, invoice: File, photos: readonly File[]): FormData {
  const body = new FormData()
  body.append('claim', new Blob([JSON.stringify(claim)], { type: 'application/json' }), 'claim.json')
  body.append('invoice', invoice, invoice.name)
  for (const photo of photos) body.append('photos', photo, photo.name)
  return body
}

/**
 * openapi-fetch types the multipart body from the contract (binary parts as strings) but sends a
 * FormData unchanged, letting the browser set the multipart boundary.
 */
export const asClaimSubmissionForm = (body: FormData) => body as unknown as Schemas['ClaimSubmissionForm']

const fieldAliases: Record<string, FormField> = {
  description: 'problemDescription',
  problemdescription: 'problemDescription',
  invoice: 'invoice',
  photos: 'photos',
}
for (const field of detailFields) fieldAliases[field.toLowerCase()] = field

/**
 * Maps a ValidationProblemDetails key (`purchase.date`, `claim.customer.email`, `$.photos[2]`, any
 * case) to a form field; `undefined` when the error belongs to no single field.
 */
export function fieldForProblemKey(key: string): FormField | undefined {
  const normalized = key
    .trim()
    .replace(/^\$\.?/, '')
    .replace(/^claim\./i, '')
    .replace(/\[\d+\]/g, '')
    .toLowerCase()
  return fieldAliases[normalized]
}

/** The claimant stepper (ui-design.md §6.6): the form covers steps 1–2, then evaluation starts. */
export function claimantFormSteps(stage: ClaimFormStep | 'submitted'): StepperStep[] {
  const state = (step: ClaimFormStep) =>
    stage === 'submitted' || stage > step ? 'done' : stage === step ? 'current' : 'upcoming'
  return [
    { key: 'details', label: 'Details', state: state(1) },
    { key: 'evidence', label: 'Evidence', state: state(2) },
    { key: 'evaluation', label: 'Evaluation', state: stage === 'submitted' ? 'current' : 'upcoming' },
    { key: 'decision', label: 'Decision', state: 'upcoming' },
  ]
}
