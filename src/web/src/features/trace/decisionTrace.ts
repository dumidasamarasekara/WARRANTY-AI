import { useQuery } from '@tanstack/react-query'
import { api, unwrap, type Schemas } from '../../shared/api/client'
import type { Tone } from '../../shared/ui'

export type DecisionTrace = Schemas['DecisionTrace']
export type TraceEntry = Schemas['TraceEntry']

/** The claim's decision trail, oldest first (`GET /api/claims/{claimId}/trace`, reviewers and auditors). */
export function useDecisionTrace(claimId: string) {
  return useQuery({
    queryKey: ['claims', claimId, 'trace'],
    queryFn: ({ signal }) => unwrap(api.GET('/api/claims/{claimId}/trace', { params: { path: { claimId } }, signal })),
  })
}

/** True when the technical view has something to show for the entry. */
export function hasTechnicalDetails(entry: TraceEntry): boolean {
  return (
    (entry.aiCalls?.length ?? 0) > 0 ||
    (entry.toolCalls?.length ?? 0) > 0 ||
    (entry.ragQueries?.length ?? 0) > 0 ||
    entry.correlationId !== undefined ||
    (entry.details !== undefined && entry.details !== null && Object.keys(entry.details).length > 0)
  )
}

export interface IntegrityPresentation {
  label: string
  tone: Tone
}

/**
 * `integrity.hashChainValid`: verified, failed, or not reported (an API that does not check the hash
 * chain omits it — never shown as verified).
 */
export function integrityPresentation(integrity: DecisionTrace['integrity'] | undefined): IntegrityPresentation {
  if (integrity?.hashChainValid === true) return { label: 'Hash chain verified', tone: 'ok' }
  if (integrity?.hashChainValid === false) return { label: 'Integrity check failed', tone: 'err' }
  return { label: 'Integrity not checked', tone: 'pending' }
}

/** Entries in `seq` order (the API sends them chronologically; this keeps the timeline stable regardless). */
export const orderedEntries = (trace: DecisionTrace): TraceEntry[] => [...trace.entries].sort((a, b) => a.seq - b.seq)
