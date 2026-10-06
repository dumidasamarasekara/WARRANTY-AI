import { useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { api, unwrap, type Schemas } from '../../shared/api/client'
import type { Actor } from '../../shared/ui'

/*
 * `GET /api/claims/{claimId}` returns more than the generated contract types describe (T085): the
 * Policy and Evidence agents' confidence, the policy assessment envelope, `clauseType` and the
 * claim's requested items. Members a role may not see are simply absent (FR-005), and an invalid
 * recommendation may lack its decision, coverage and confidence.
 */

export interface ConsistencyRow {
  field: string
  claimValue?: string | null
  evidenceValue?: string | null
  match: boolean
}

export interface EvidenceFinding {
  ref?: string
  evidenceId?: string
  kind: 'InvoiceExtraction' | 'PhotoAnalysis'
  /** invoice-extraction.schema.json or photo-analysis.schema.json */
  result?: Record<string, unknown> | null
  consistency?: ConsistencyRow[]
  /** The Evidence agent's confidence (photo analysis only). */
  confidence?: number
}

export interface PolicyAssessment {
  versionOutcome?: 'Ok' | 'NoApplicablePolicy' | 'AmbiguousPolicyVersion'
  /** policy-assessment.schema.json */
  assessment?: Record<string, unknown> | null
  /** The Policy agent's confidence in its coverage assessment. */
  confidence?: number
  model?: string
  promptVersion?: string
}

export type PolicyReference = Schemas['PolicyReference'] & { clauseType?: string }

export type Recommendation = Omit<Schemas['Recommendation'], 'decision' | 'coverage' | 'confidence' | 'reasoningSummary'> &
  Partial<Pick<Schemas['Recommendation'], 'decision' | 'coverage' | 'confidence' | 'reasoningSummary'>>

export type ClaimEvaluation = Omit<
  Schemas['Evaluation'],
  'evidenceFindings' | 'policyAssessment' | 'policyReferences' | 'recommendation' | 'extraction'
> & {
  extraction?: Record<string, unknown> | null
  evidenceFindings?: EvidenceFinding[]
  policyAssessment?: PolicyAssessment
  policyReferences?: PolicyReference[]
  recommendation?: Recommendation
}

export type ClaimDetail = Omit<Schemas['ClaimDetail'], 'latestEvaluation'> & {
  latestEvaluation?: ClaimEvaluation
  requestedItems?: Schemas['RequestedItem'][]
}

export type EvidenceItem = Schemas['EvidenceItem']

export const claimQueryKey = (claimId: string) => ['claims', claimId] as const

/** The staff view of one claim (FR-033). */
export function useClaimDetail(claimId: string) {
  return useQuery({
    queryKey: claimQueryKey(claimId),
    queryFn: async ({ signal }) =>
      (await unwrap(api.GET('/api/claims/{claimId}', { params: { path: { claimId } }, signal }))) as unknown as ClaimDetail,
  })
}

export const isPdf = (item: Pick<EvidenceItem, 'contentType'>) => item.contentType === 'application/pdf'

export const evidenceKindLabel = (kind: EvidenceItem['kind']) => (kind === 'Other' ? 'Other file' : kind)

/**
 * Evidence content fetched with the staff bearer token (an `<img src>` could not send it) and shown
 * from an object URL, revoked when the item changes or the component unmounts.
 */
export function useEvidenceContentUrl(claimId: string, evidenceId: string | undefined, enabled = true) {
  const content = useQuery({
    queryKey: ['claims', claimId, 'evidence', evidenceId, 'content'],
    queryFn: ({ signal }) =>
      unwrap(
        api.GET('/api/claims/{claimId}/evidence/{evidenceId}/content', {
          params: { path: { claimId, evidenceId: evidenceId! } },
          parseAs: 'blob',
          signal,
        }),
      ) as Promise<Blob>,
    enabled: enabled && evidenceId !== undefined,
    staleTime: Infinity,
  })
  const [url, setUrl] = useState<string>()
  const blob = content.data

  useEffect(() => {
    if (!blob) return
    const objectUrl = URL.createObjectURL(blob)
    // The object URL is an external resource owned by this effect: created here and revoked in its
    // cleanup, so it survives StrictMode's remount (a memoized URL would be revoked and not recreated).
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setUrl(objectUrl)
    return () => {
      URL.revokeObjectURL(objectUrl)
      setUrl(undefined)
    }
  }, [blob])

  const status: 'loading' | 'loaded' | 'error' = content.isError ? 'error' : url ? 'loaded' : 'loading'
  return { url, status }
}

/** The finding about one evidence file, matched by ID (or by its `EV-n`). */
export function findingFor(evaluation: ClaimEvaluation | undefined, item: EvidenceItem): EvidenceFinding | undefined {
  return evaluation?.evidenceFindings?.find(
    (finding) => finding.evidenceId === item.evidenceId || (item.ref !== undefined && finding.ref === item.ref),
  )
}

export type ProgressState = 'done' | 'current' | 'pending'

export interface ProgressStep {
  key: string
  label: string
  actor: Actor
  state: ProgressState
}

const finalStatuses = new Set<ClaimDetail['status']>(['Approved', 'Rejected'])

/**
 * The claim header's progress strip (ui-design.md §6.3): Submitted → Intake → Evidence → Policy →
 * Risk → Decision → Guardrails → Outcome (→ Review when escalated). A step is done when its part of
 * the latest evaluation is present, or a later step is (risk is absent from a claims agent's view);
 * the first step not done is current unless the claim is finalized.
 */
export function progressSteps(detail: ClaimDetail): ProgressStep[] {
  const evaluation = detail.latestEvaluation
  const disposition = evaluation?.guardrails?.disposition
  const escalated = disposition === 'HumanReview' || (detail.reviewDecisions?.length ?? 0) > 0
  const reviewed = detail.finalDecidedBy === 'Reviewer' || (detail.reviewDecisions?.length ?? 0) > 0

  const steps: Array<Omit<ProgressStep, 'state'> & { present: boolean }> = [
    { key: 'submitted', label: 'Submitted', actor: 'system', present: true },
    {
      key: 'intake',
      label: 'Intake',
      actor: 'ai',
      present: !!evaluation?.extraction || (evaluation?.validation?.length ?? 0) > 0,
    },
    { key: 'evidence', label: 'Evidence', actor: 'ai', present: (evaluation?.evidenceFindings?.length ?? 0) > 0 },
    {
      key: 'policy',
      label: 'Policy',
      actor: 'ai',
      present: !!evaluation?.policyAssessment || (evaluation?.policyReferences?.length ?? 0) > 0,
    },
    { key: 'risk', label: 'Risk', actor: 'system', present: !!evaluation?.risk },
    { key: 'decision', label: 'Decision', actor: 'ai', present: !!evaluation?.recommendation },
    { key: 'guardrails', label: 'Guardrails', actor: 'system', present: !!evaluation?.guardrails },
    { key: 'outcome', label: 'Outcome', actor: escalated ? 'human' : 'system', present: !!disposition },
  ]
  if (escalated) steps.push({ key: 'review', label: 'Review', actor: 'human', present: reviewed })

  // A later step being present means the earlier ones ran, even when the caller may not see them.
  let laterDone = finalStatuses.has(detail.status) && !escalated
  for (let index = steps.length - 1; index >= 0; index--) {
    const step = steps[index]!
    step.present ||= laterDone
    laterDone = step.present
  }

  const finalized = finalStatuses.has(detail.status)
  let currentAssigned = false
  return steps.map(({ present, ...step }) => {
    if (present) return { ...step, state: 'done' }
    if (!finalized && !currentAssigned) {
      currentAssigned = true
      return { ...step, state: 'current' }
    }
    return { ...step, state: 'pending' }
  })
}

/** `SCREEN_PHYSICAL_DAMAGE` → "Screen physical damage" for AI-extracted enum values. */
export function humanize(value: string): string {
  const words = value.replace(/_/g, ' ').toLowerCase()
  return words.charAt(0).toUpperCase() + words.slice(1)
}
