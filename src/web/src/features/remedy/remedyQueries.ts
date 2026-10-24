import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap, type Schemas } from '../../shared/api/client'
import { claimQueryKey } from '../claims/claimDetail'

/*
 * The remedy of an approved claim arrives inside `GET /api/claims/{claimId}` (`ClaimDetail.remedy`,
 * 002 contracts) with its own ETag, so the Remedy tab reads it through the claim query and only the
 * decision needs its own call.
 */

export type RemedyView = Schemas['RemedyView']

export type RemedyHistoryEntry = RemedyView['history'][number]

export type RemedyDecisionRequest = Schemas['RemedyDecisionRequest']

export interface DecideRemedyInput {
  claimId: string
  /** `RemedyView.etag`, sent as `If-Match`. */
  etag: string
  request: RemedyDecisionRequest
}

/**
 * `POST /api/claims/{claimId}/remedy-decisions` with `If-Match`; failures throw an `ApiProblem`. A
 * recorded decision reloads the claim (header progress step, remedy card and its new ETag).
 */
export function useDecideRemedy() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ claimId, etag, request }: DecideRemedyInput) =>
      unwrap(
        api.POST('/api/claims/{claimId}/remedy-decisions', {
          params: { path: { claimId }, header: { 'If-Match': etag } },
          body: request,
        }),
      ),
    onSuccess: (_remedy, { claimId }) => queryClient.invalidateQueries({ queryKey: claimQueryKey(claimId) }),
  })
}
