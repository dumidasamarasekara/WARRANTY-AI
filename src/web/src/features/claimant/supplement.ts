import type { Schemas } from '../../shared/api/client'
import { maxPhotos } from './claimSubmission'

export type RequestedItem = Schemas['RequestedItem']

/** Limits of `SupplementForm` in contracts/rest-api.openapi.yaml; files follow the submission rules. */
export const supplementNoteMax = 2000

export type SupplementField = 'note' | 'invoice' | 'photos'
export type SupplementErrors = Partial<Record<SupplementField | 'form', string>>

/** A supplement (FR-010): an optional note, invoice and up to 8 photos, but at least one of them. */
export function validateSupplement(note: string, invoice: File | null, photos: readonly File[]): SupplementErrors {
  const errors: SupplementErrors = {}
  const length = note.trim().length
  if (length > supplementNoteMax) errors.note = `Use at most ${supplementNoteMax.toLocaleString('en-US')} characters`
  if (photos.length > maxPhotos) errors.photos = `Add at most ${maxPhotos} photos`
  if (length === 0 && !invoice && photos.length === 0) errors.form = 'Add a note, the invoice or at least one photo'
  return errors
}

/** `SupplementForm`: a `note` text part when there is one, an `invoice` part and one `photos` part per photo. */
export function buildSupplementFormData(note: string, invoice: File | null, photos: readonly File[]): FormData {
  const body = new FormData()
  const trimmed = note.trim()
  if (trimmed) body.append('note', trimmed)
  if (invoice) body.append('invoice', invoice, invoice.name)
  for (const photo of photos) body.append('photos', photo, photo.name)
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
  return normalized === 'note' || normalized === 'invoice' || normalized === 'photos' ? normalized : undefined
}
