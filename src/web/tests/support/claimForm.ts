import { fireEvent, screen } from '@testing-library/react'
import type { UserEvent } from '@testing-library/user-event'

/** Valid step-1 values, by field label. */
export const validDetails = {
  'Full name': 'Alex Example',
  'Email address': 'alex@example.test',
  'Phone number': '+1 555 0100',
  Country: 'us',
  'Model code': 'AP7-220',
  'Serial number': 'SN-0001',
  'Place of purchase': 'Example Store',
  'Price paid': '499.99',
  Currency: 'usd',
  'Country of purchase': 'us',
} as const

export const validPurchaseDate = '2025-01-15'
export const validDescription = 'The control panel stopped responding after two weeks of normal use.'

export function file(name: string, type: string, size = 1024): File {
  return new File([new Uint8Array(size)], name, { type })
}

export const textbox = (name: string | RegExp) => screen.getByRole('textbox', { name })

export async function fillDetails(user: UserEvent, overrides: Partial<Record<keyof typeof validDetails, string>> = {}) {
  const values = { ...validDetails, ...overrides }
  for (const [label, value] of Object.entries(values)) {
    if (value) await user.type(textbox(label), value)
  }
  fireEvent.change(screen.getByLabelText(/Purchase date/), { target: { value: validPurchaseDate } })
}

export async function fillEvidence(user: UserEvent, photos: File[] = [file('front.jpg', 'image/jpeg'), file('label.png', 'image/png')]) {
  await user.type(textbox(/What went wrong|Problem description/), validDescription)
  await user.upload(screen.getByLabelText('Invoice'), file('invoice.pdf', 'application/pdf'))
  await user.upload(screen.getByLabelText(/Photos of the product/), photos)
}
