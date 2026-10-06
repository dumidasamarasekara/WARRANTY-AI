import { describe, expect, it } from 'vitest'
import {
  buildSupplementFormData,
  supplementFieldForProblemKey,
  validateSupplement,
} from '../../../src/features/claimant/supplement'
import { requestedItemOptions } from '../../../src/features/review/reviewDecision'
import { requestedItemLabel } from '../../../src/shared/presentation'
import { file } from '../../support/claimForm'

describe('validateSupplement', () => {
  it('needs a note, the invoice or a photo', () => {
    expect(validateSupplement('   ', null, [])).toEqual({ form: 'Add a note, the invoice or at least one photo' })
    expect(validateSupplement('Bought on 15 January.', null, [])).toEqual({})
    expect(validateSupplement('', file('invoice.pdf', 'application/pdf'), [])).toEqual({})
    expect(validateSupplement('', null, [file('label.jpg', 'image/jpeg')])).toEqual({})
  })

  it('limits the note to 2,000 characters and the photos to 8', () => {
    const photos = Array.from({ length: 9 }, (_, index) => file(`photo-${index}.jpg`, 'image/jpeg'))
    expect(validateSupplement('x'.repeat(2001), null, photos)).toEqual({
      note: 'Use at most 2,000 characters',
      photos: 'Add at most 8 photos',
    })
    expect(validateSupplement('x'.repeat(2000), null, [])).toEqual({})
  })
})

describe('buildSupplementFormData', () => {
  it('sends the trimmed note, the invoice and one part per photo, leaving out what is empty', () => {
    const body = buildSupplementFormData('  The serial label is on the back.  ', null, [
      file('label.jpg', 'image/jpeg'),
      file('back.png', 'image/png'),
    ])
    expect(body.get('note')).toBe('The serial label is on the back.')
    expect(body.has('invoice')).toBe(false)
    expect(body.getAll('photos').map((part) => (part as File).name)).toEqual(['label.jpg', 'back.png'])

    const invoiceOnly = buildSupplementFormData(' ', file('invoice.pdf', 'application/pdf'), [])
    expect(invoiceOnly.has('note')).toBe(false)
    expect((invoiceOnly.get('invoice') as File).name).toBe('invoice.pdf')
  })
})

describe('supplementFieldForProblemKey', () => {
  it('maps problem keys to fields in any case and ignores the rest', () => {
    expect(supplementFieldForProblemKey('Note')).toBe('note')
    expect(supplementFieldForProblemKey('$.photos[1]')).toBe('photos')
    expect(supplementFieldForProblemKey('invoice')).toBe('invoice')
    expect(supplementFieldForProblemKey('claim')).toBeUndefined()
  })
})

describe('requestedItemLabel', () => {
  it('labels the item codes like the API catalog and treats unknown codes as other information', () => {
    expect(requestedItemLabel('INVOICE')).toBe('Invoice or receipt')
    expect(requestedItemLabel(' photo_of_serial_label ')).toBe('Photo of the serial number label')
    expect(requestedItemLabel('OTHER')).toBe('Other information')
    expect(requestedItemLabel('SOMETHING_NEW')).toBe('Other information')
    expect(requestedItemLabel(undefined)).toBe('Other information')
  })

  it('uses the labels of the reviewer picker', () => {
    for (const option of requestedItemOptions.filter((candidate) => candidate.item !== 'OTHER')) {
      expect(requestedItemLabel(option.item)).toBe(option.label)
    }
  })
})
