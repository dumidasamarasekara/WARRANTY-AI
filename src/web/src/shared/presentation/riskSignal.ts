/**
 * Readable labels of the risk-signal wire codes (`RiskSignalCode`; 001 research R23, 003 research R11). Signals are
 * shown with their code, source badge and severity; the label says in words what the code means.
 */
const riskSignals: Record<string, string> = {
  SOURCE_INCONSISTENCY: 'Invoice does not match the claim',
  PRODUCT_NOT_IN_CATALOG: "Product or serial not in the tenant's catalog",
  SERIAL_MISMATCH_PHOTO: 'Serial in a photo differs from the claimed serial',
  DUPLICATE_SERIAL_CLAIM: 'Another recent claim for this serial',
  EVIDENCE_REUSED: 'Evidence identical to another claim',
  DAMAGE_INCONSISTENT_WITH_DESCRIPTION: 'Damage does not match the description',
  PURCHASE_DATE_ANOMALY: 'Purchase date anomaly',
  MANIPULATION_ATTEMPT: 'Text addressed to the adjudication system',
  OTHER: 'Other risk',
  PRODUCT_RETURNED_AND_REPLACED: 'Product was returned and replaced',
}

/** Label for a `risk.signals[].code`; a code without a label is shown as-is. */
export function riskSignalLabel(code: string): string {
  return riskSignals[code] ?? code
}
