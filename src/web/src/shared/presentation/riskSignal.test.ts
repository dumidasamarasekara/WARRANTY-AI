import { describe, expect, it } from 'vitest'
import { riskSignalLabel } from './riskSignal'

describe('risk signal labels', () => {
  it.each<[string, string]>([
    ['PRODUCT_RETURNED_AND_REPLACED', 'Product was returned and replaced'],
    ['PRODUCT_NOT_IN_CATALOG', "Product or serial not in the tenant's catalog"],
    ['DUPLICATE_SERIAL_CLAIM', 'Another recent claim for this serial'],
  ])('%s → %s', (code, label) => {
    expect(riskSignalLabel(code)).toBe(label)
  })

  it('shows an unknown code as-is', () => {
    expect(riskSignalLabel('SOMETHING_NEW')).toBe('SOMETHING_NEW')
  })
})
