import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
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

export type RepairView = Schemas['RepairView']

export type RepairEvent = RepairView['events'][number]

export type RepairEventType = Schemas['RepairEventType']

export type RepairEventRequest = Schemas['RepairEventRequest']

export type RepairCentre = Schemas['RepairCentre']

export type ReportSource = Schemas['ReportSource']

export const repairCentresQueryKey = (claimId: string) => ['claims', claimId, 'repair-centres'] as const

/** `GET /api/claims/{claimId}/repair-centres`: eligible centres, those serving the claim first. */
export function useRepairCentres(claimId: string, enabled = true) {
  return useQuery({
    queryKey: repairCentresQueryKey(claimId),
    queryFn: () => unwrap(api.GET('/api/claims/{claimId}/repair-centres', { params: { path: { claimId } } })),
    enabled,
  })
}

export interface RecordRepairEventInput {
  claimId: string
  /** `RepairView.etag`, sent as `If-Match`. */
  etag: string
  request: RepairEventRequest
}

/**
 * `POST /api/claims/{claimId}/repair/events` with `If-Match`; failures throw an `ApiProblem` (409 with the
 * current status and allowed events, 412 when the repair changed). A recorded event reloads the claim.
 */
export function useRecordRepairEvent() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ claimId, etag, request }: RecordRepairEventInput) =>
      unwrap(
        api.POST('/api/claims/{claimId}/repair/events', {
          params: { path: { claimId }, header: { 'If-Match': etag } },
          body: request,
        }),
      ),
    onSuccess: (_repair, { claimId }) => queryClient.invalidateQueries({ queryKey: claimQueryKey(claimId) }),
  })
}