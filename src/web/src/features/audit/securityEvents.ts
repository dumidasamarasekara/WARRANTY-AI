import type { Schemas } from '../../shared/api/client'
import type { Labelled } from '../../shared/ui/tone'

export type SecurityEventKind = Schemas['SecurityEventKind']

/** Tenant-visible kinds in filter order; operator-only kinds never reach the API response. */
export const securityEventKinds: readonly SecurityEventKind[] = [
  'ACCESS_DENIED',
  'CLAIMANT_ACCESS_FAILED',
  'SELF_REVIEW_REFUSED',
  'RETRIEVAL_SCOPE_VIOLATION',
  'TOOL_SCOPE_VIOLATION',
]

const kinds: Record<SecurityEventKind, Labelled> = {
  ACCESS_DENIED: { label: 'Access denied', tone: 'warn' },
  CLAIMANT_ACCESS_FAILED: { label: 'Claimant access failed', tone: 'warn' },
  SELF_REVIEW_REFUSED: { label: 'Self-review refused', tone: 'human' },
  RETRIEVAL_SCOPE_VIOLATION: { label: 'AI scope violation', tone: 'err' },
  TOOL_SCOPE_VIOLATION: { label: 'AI scope violation', tone: 'err' },
}

/** Badge label and tone of a security event kind (ui-design.md §6.7); unknown kinds show as-is. */
export const securityEventKindPresentation = (kind: string): Labelled =>
  kinds[kind as SecurityEventKind] ?? { label: kind, tone: 'pending' }

/** Filter option label: the two AI scope kinds share a badge, so the filter names which one. */
export function securityEventKindOptionLabel(kind: SecurityEventKind): string {
  if (kind === 'RETRIEVAL_SCOPE_VIOLATION') return 'AI scope violation (retrieval)'
  if (kind === 'TOOL_SCOPE_VIOLATION') return 'AI scope violation (tool)'
  return kinds[kind].label
}

export const isSecurityEventKind = (value: string | null): value is SecurityEventKind =>
  securityEventKinds.includes(value as SecurityEventKind)
