/** Readable labels of the guardrails' `EscalationReason` wire codes (data-model.md, `EscalationReason.Label`). */
const escalationReasons: Record<string, string> = {
  VALUE_ABOVE_LIMIT: 'Claim value above auto-approval limit',
  CONFIDENCE_BELOW_MIN: "Confidence below the tenant's minimum",
  ALWAYS_REVIEW_CATEGORY: 'Product category always requires a human decision',
  RISK_MEDIUM: 'Medium risk',
  RISK_HIGH: 'High risk',
  EVIDENCE_CONFLICT: 'Conflicting evidence',
  AI_DETERMINISTIC_DISAGREEMENT: 'AI recommendation conflicts with an independent check',
  INVALID_RECOMMENDATION: 'AI recommendation invalid',
  AI_UNAVAILABLE: 'AI analysis could not be completed',
  AI_RECOMMENDS_REVIEW: 'AI recommends human review',
  NO_APPLICABLE_POLICY: 'No applicable policy found',
  AMBIGUOUS_POLICY: 'Policy applicability is ambiguous',
  PRODUCT_NOT_IN_CATALOG: "Product or serial not in the tenant's catalog",
  RETURNED_AFTER_REVIEWER_REQUEST: 'Returned after reviewer information request',
  INFO_INCOMPLETE_AFTER_2_REQUESTS: 'Information still incomplete after 2 requests',
  UNSAFE_CLAIMANT_TEXT: 'Claimant explanation needs a reviewer',
}

/**
 * Label for a `guardrails.reasons` entry. The claim detail returns wire codes, except for claims
 * agents, whose risk-related reasons arrive already collapsed into "Additional checks required"
 * (FR-005); text that is not a known code is shown as-is.
 */
export function escalationReasonLabel(reason: string): string {
  return escalationReasons[reason] ?? reason
}
