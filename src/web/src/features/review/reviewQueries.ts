import { useMutation, useQuery } from '@tanstack/react-query'
import { api, unwrap, type Schemas } from '../../shared/api/client'
import type { ClaimDetail } from './reviewDecision'

/** Same key as the sidebar count badge, so a recorded decision refreshes both. */
export const reviewQueueKey = ['review-queue'] as const

export const reviewClaimKey = (claimId: string) => ['review-claim', claimId] as const

/** Escalated claims of the reviewer's tenant, oldest first (`GET /api/review-queue`). */
export function useReviewQueue() {
  return useQuery({
    queryKey: reviewQueueKey,
    queryFn: ({ signal }) => unwrap(api.GET('/api/review-queue', { signal })),
  })
}

export interface ReviewClaim {
  detail: ClaimDetail
  /** The claim's row version; sent back as `If-Match` with the decision. */
  etag: string
}

/** The claim under review with its ETag (`GET /api/claims/{claimId}`). */
export function useReviewClaim(claimId: string) {
  return useQuery({
    queryKey: reviewClaimKey(claimId),
    queryFn: async ({ signal }): Promise<ReviewClaim> => {
      const result = await api.GET('/api/claims/{claimId}', { params: { path: { claimId } }, signal })
      const detail = await unwrap(Promise.resolve(result))
      return { detail, etag: result.response.headers.get('ETag') ?? '' }
    },
  })
}

export interface RecordDecisionInput {
  claimId: string
  etag: string
  request: Schemas['ReviewDecisionRequest']
}

/** `POST /api/claims/{claimId}/review-decisions` with `If-Match`; failures throw an `ApiProblem`. */
export function useRecordReviewDecision() {
  return useMutation({
    mutationFn: ({ claimId, etag, request }: RecordDecisionInput) =>
      unwrap(
        api.POST('/api/claims/{claimId}/review-decisions', {
          params: { path: { claimId }, header: { 'If-Match': etag } },
          body: request,
        }),
      ),
  })
}
