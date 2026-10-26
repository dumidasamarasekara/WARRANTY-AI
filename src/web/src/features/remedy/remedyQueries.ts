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

export type RepairStatus = Schemas['RepairStatus']

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

export type ReplacementView = Schemas['ReplacementView']

export type ReplacementEvent = ReplacementView['events'][number]

export type ReplacementEventType = Schemas['ReplacementEventType']

export type ReplacementEventRequest = Schemas['ReplacementEventRequest']

export const replacementLocationsQueryKey = (claimId: string) => ['claims', claimId, 'replacement-locations'] as const

/** `GET /api/claims/{claimId}/replacement-locations`: eligible locations, those serving the claim first (003). */
export function useReplacementLocations(claimId: string, enabled = true) {
  return useQuery({
    queryKey: replacementLocationsQueryKey(claimId),
    queryFn: () => unwrap(api.GET('/api/claims/{claimId}/replacement-locations', { params: { path: { claimId } } })),
    enabled,
  })
}

export type ReplacementModel = Schemas['ReplacementModel']

export const replacementModelsQueryKey = (claimId: string) => ['claims', claimId, 'replacement-models'] as const

/**
 * `GET /api/claims/{claimId}/replacement-models`: the models staff may propose instead of the claimed one (003 FR-023);
 * empty when the policy allows the same model only.
 */
export function useReplacementModels(claimId: string, enabled = true) {
  return useQuery({
    queryKey: replacementModelsQueryKey(claimId),
    queryFn: () => unwrap(api.GET('/api/claims/{claimId}/replacement-models', { params: { path: { claimId } } })),
    enabled,
  })
}

export interface RecordReplacementEventInput {
  claimId: string
  /** `ReplacementView.etag`, sent as `If-Match`. */
  etag: string
  request: ReplacementEventRequest
}

/**
 * `POST /api/claims/{claimId}/replacement/events` with `If-Match`; failures throw an `ApiProblem` (409 with the
 * current status and allowed events, 412 when the replacement changed, 403 for a reviewer-only event). A recorded
 * event reloads the claim.
 */
export function useRecordReplacementEvent() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ claimId, etag, request }: RecordReplacementEventInput) =>
      unwrap(
        api.POST('/api/claims/{claimId}/replacement/events', {
          params: { path: { claimId }, header: { 'If-Match': etag } },
          body: request,
        }),
      ),
    onSuccess: (_replacement, { claimId }) => queryClient.invalidateQueries({ queryKey: claimQueryKey(claimId) }),
  })
}

export type RemedyCompletionRequest = Schemas['RemedyCompletionRequest']

export type RemedyCompletion = NonNullable<RemedyView['completion']>

export interface RecordRemedyCompletionInput {
  claimId: string
  /** `RemedyView.etag`, sent as `If-Match`. */
  etag: string
  request: RemedyCompletionRequest
}

/**
 * `POST /api/claims/{claimId}/remedy-completions` with `If-Match`; failures throw an `ApiProblem` (409 when
 * the remedy is no longer in progress, 412 when it changed). A recorded completion reloads the claim.
 */
export function useRecordRemedyCompletion() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ claimId, etag, request }: RecordRemedyCompletionInput) =>
      unwrap(
        api.POST('/api/claims/{claimId}/remedy-completions', {
          params: { path: { claimId }, header: { 'If-Match': etag } },
          body: request,
        }),
      ),
    onSuccess: (_remedy, { claimId }) => queryClient.invalidateQueries({ queryKey: claimQueryKey(claimId) }),
  })
}
