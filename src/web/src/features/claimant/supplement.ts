import type { Schemas } from '../../shared/api/client'
import { maxPhotos } from './claimSubmission'

export type RequestedItem = Schemas['RequestedItem']

/** Limits of `SupplementForm` in contracts/rest-api.openapi.yaml; files follow the submission rules. */
export const supplementNoteMax = 2000

export type SupplementField = 'note' | 'invoice' | 'photos' | 'protectionPlanDocument'
export type SupplementErrors = Partial<Record<SupplementField | 'form', string>>

/** The requested-item code under which a claim asks for the protection plan document (002 FR-036, FR-037). */
export const protectionPlanDocumentItem = 'PROTECTION_PLAN_DOCUMENT'

/** Whether the claim asks for the protection plan document, so the form offers a place for it. */
export function requestsProtectionPlanDocument(requestedItems: readonly RequestedItem[]): boolean {
  return requestedItems.some((requested) => requested.item.trim().toUpperCase() === protectionPlanDocumentItem)
}

/**
 * A supplement (FR-010): an optional note, invoice, up to 8 photos and, when requested, the
 * protection plan document, but at least one of them. `planRequested` words the "add something"
 * message to include the plan document.
 */
export function validateSupplement(
  note: string,
  invoice: File | null,
  photos: readonly File[],
  planDocument: File | null = null,
  planRequested = false,
): SupplementErrors {
  const errors: SupplementErrors = {}
  const length = note.trim().length
  if (length > supplementNoteMax) errors.note = `Use at most ${supplementNoteMax.toLocaleString('en-US')} characters`
  if (photos.length > maxPhotos) errors.photos = `Add at most ${maxPhotos} photos`
  if (length === 0 && !invoice && photos.length === 0 && !planDocument) {
    errors.form = planRequested
      ? 'Add a note, the invoice, at least one photo or the protection plan document'
      : 'Add a note, the invoice or at least one photo'
  }
  return errors
}

/**
 * `SupplementForm`: a `note` text part when there is one, an `invoice` part, one `photos` part per
 * photo and a `protectionPlanDocument` part when the claimant added the plan document.
 */
export function buildSupplementFormData(
  note: string,
  invoice: File | null,
  photos: readonly File[],
  planDocument: File | null = null,
): FormData {
  const body = new FormData()
  const trimmed = note.trim()
  if (trimmed) body.append('note', trimmed)
  if (invoice) body.append('invoice', invoice, invoice.name)
  for (const photo of photos) body.append('photos', photo, photo.name)
  if (planDocument) body.append('protectionPlanDocument', planDocument, planDocument.name)
  return body
}

/** openapi-fetch types the multipart body from the contract but sends a FormData unchanged. */
export const asSupplementForm = (body: FormData) => body as unknown as Schemas['SupplementForm']

/** Maps a ValidationProblemDetails key (`note`, `$.photos[1]`, any case) to a supplement field. */
export function supplementFieldForProblemKey(key: string): SupplementField | undefined {
  const normalized = key
    .trim()
    .replace(/^\$\.?/, '')
    .replace(/\[\d+\]/g, '')
    .toLowerCase()
  if (normalized === 'protectionplandocument') return 'protectionPlanDocument'
  return normalized === 'note' || normalized === 'invoice' || normalized === 'photos' ? normalized : undefined
}
