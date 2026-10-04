import { useQuery } from '@tanstack/react-query'
import { api, unwrap } from '../shared/api/client'
import { useStaffSession } from './useStaffSession'

/** The tenant (display name, currency) and roles of the signed-in staff user. */
export function useMe() {
  const session = useStaffSession()
  return useQuery({
    queryKey: ['me'],
    queryFn: ({ signal }) => unwrap(api.GET('/api/me', { signal })),
    enabled: session !== null,
    staleTime: 5 * 60_000,
  })
}

/** Number of escalated claims waiting in the reviewer's queue (sidebar count badge). */
export function useReviewQueueCount(enabled: boolean) {
  const session = useStaffSession()
  return useQuery({
    queryKey: ['review-queue'],
    queryFn: ({ signal }) => unwrap(api.GET('/api/review-queue', { signal })),
    select: (items) => items.length,
    enabled: enabled && session !== null,
  })
}
