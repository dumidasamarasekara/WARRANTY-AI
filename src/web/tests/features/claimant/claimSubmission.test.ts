import { describe, expect, it } from 'vitest'
import {
  checkFile,
  emptyClaimFormValues,
  fieldForProblemKey,
  toSubmissionData,
  validateDetails,
  validateEvidence,
  type ClaimFormValues,
} from '../../../src/features/claimant/claimSubmission'

const valid: ClaimFormValues = {
  ...emptyClaimFormValues,
  'customer.fullName': ' Alex Example ',
  'customer.email': 'alex@example.test',
  'customer.country': 'us',
  'product.modelCode': 'AP7-220',
  'product.serialNumber': 'SN-0001',
  'purchase.date': '2026-10-04',
  'purchase.place': 'Example Store',
  'purchase.price': '499.99',
  'purchase.currency': 'usd',
  'purchase.country': 'US',
  problemDescription: 'The control panel stopped responding.',
}

const today = '2026-10-04'
const photo = (name: string, type: string, size = 10) => new File([new Uint8Array(size)], name, { type })

describe('validateDetails', () => {
  it('accepts complete details with a purchase on the claim date', () => {
    expect(validateDetails(valid, today)).toEqual({})
  })

  it('requires the fields the API requires and leaves optional ones alone', () => {
    const errors = validateDetails(emptyClaimFormValues, today)
    expect(Object.keys(errors).sort()).toEqual(
      [
        'customer.country',
        'customer.email',
        'customer.fullName',
        'product.modelCode',
        'product.serialNumber',
        'purchase.country',
        'purchase.currency',
        'purchase.date',
        'purchase.place',
        'purchase.price',
      ].sort(),
    )
  })

  it('rejects a purchase date after the claim date and impossible dates (FR-009)', () => {
    expect(validateDetails({ ...valid, 'purchase.date': '2026-10-05' }, today)['purchase.date']).toBe(
      "The purchase date can't be in the future or after the claim date",
    )
    expect(validateDetails({ ...valid, 'purchase.date': '2026-02-30' }, today)['purchase.date']).toBe('Enter a valid date')
  })

  it('checks codes, price, email and lengths like the contract', () => {
    const errors = validateDetails(
      {
        ...valid,
        'customer.email': 'alex@',
        'customer.country': 'USA',
        'purchase.currency': 'US',
        'purchase.price': '0',
        'product.serialNumber': 'S'.repeat(51),
      },
      today,
    )
    expect(errors).toEqual({
      'customer.email': 'Enter an email address like name@example.com',
      'customer.country': 'Use the two-letter country code, e.g. US',
      'purchase.currency': 'Use the three-letter currency code, e.g. USD',
      'purchase.price': 'Enter the price as a number greater than 0, e.g. 499.99',
      'product.serialNumber': 'Use at most 50 characters',
    })
  })
})

describe('validateEvidence', () => {
  const invoice = photo('invoice.pdf', 'application/pdf')

  it('needs 20–4,000 characters, an invoice and 1–8 photos', () => {
    expect(validateEvidence(valid, invoice, [photo('a.jpg', 'image/jpeg')])).toEqual({})
    expect(validateEvidence({ ...valid, problemDescription: 'Broken' }, null, [])).toEqual({
      problemDescription: 'Describe the problem in at least 20 characters',
      invoice: 'Add the invoice',
      photos: 'Add at least one photo of the product',
    })
    const nine = Array.from({ length: 9 }, (_, index) => photo(`${index}.jpg`, 'image/jpeg'))
    expect(validateEvidence({ ...valid, problemDescription: 'x'.repeat(4001) }, invoice, nine)).toEqual({
      problemDescription: 'Use at most 4,000 characters',
      photos: 'Add at most 8 photos',
    })
  })
})

describe('checkFile', () => {
  it('allows JPEG, PNG, WebP and PDF up to 15 MB', () => {
    expect(checkFile(photo('a.jpg', 'image/jpeg'))).toBeUndefined()
    expect(checkFile(photo('a.webp', 'image/webp'))).toBeUndefined()
    expect(checkFile(photo('scan.pdf', ''))).toBeUndefined()
    expect(checkFile(photo('big.png', 'image/png', 15 * 1024 * 1024 + 1))).toBe('big.png is larger than 15 MB')
  })

  it('asks for JPEG or PNG instead of HEIC and refuses other types', () => {
    expect(checkFile(photo('IMG_1.HEIC', ''))).toBe('Please send photos as JPEG or PNG')
    expect(checkFile(photo('IMG_1.jpg', 'image/heif'))).toBe('Please send photos as JPEG or PNG')
    expect(checkFile(photo('notes.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'))).toBe(
      "notes.docx can't be used: send a PDF, JPG, PNG or WebP file",
    )
  })
})

describe('toSubmissionData', () => {
  it('trims values, upper-cases codes, parses the price and leaves out empty optional fields', () => {
    expect(toSubmissionData(valid)).toEqual({
      customer: { fullName: 'Alex Example', email: 'alex@example.test', country: 'US' },
      product: { modelCode: 'AP7-220', serialNumber: 'SN-0001' },
      purchase: { date: '2026-10-04', place: 'Example Store', price: 499.99, currency: 'USD', country: 'US' },
      problemDescription: 'The control panel stopped responding.',
    })
  })
})

describe('fieldForProblemKey', () => {
  it('maps API error keys in any casing and prefix to form fields', () => {
    expect(fieldForProblemKey('purchase.date')).toBe('purchase.date')
    expect(fieldForProblemKey('Claim.Customer.Email')).toBe('customer.email')
    expect(fieldForProblemKey('$.photos[2]')).toBe('photos')
    expect(fieldForProblemKey('claim.description')).toBe('problemDescription')
    expect(fieldForProblemKey('tenantId')).toBeUndefined()
  })
})
