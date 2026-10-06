/**
 * Short labels of the requested-item codes (FR-010, FR-029). They match the API's
 * `RequestedItemCatalog`, which already turns every stored item into one of these codes with a
 * claimant-safe reason, so screens show the label and never the raw code.
 */
export const requestedItemLabels = {
  INVOICE: 'Invoice or receipt',
  LEGIBLE_INVOICE: 'Clearer copy of the invoice',
  PHOTO_OF_DAMAGE: 'Photos of the damage',
  PHOTO_OF_SERIAL_LABEL: 'Photo of the serial number label',
  PURCHASE_DATE: 'Purchase date',
  PROBLEM_DETAILS: 'More detail about the problem',
  OTHER: 'Other information',
} as const

export type RequestedItemCode = keyof typeof requestedItemLabels

const isCode = (value: string): value is RequestedItemCode => Object.hasOwn(requestedItemLabels, value)

/** The label of an item code (any case); an unknown code reads as "Other information", as in the API. */
export function requestedItemLabel(item: string | undefined): string {
  const code = (item ?? '').trim().toUpperCase()
  return isCode(code) ? requestedItemLabels[code] : requestedItemLabels.OTHER
}
