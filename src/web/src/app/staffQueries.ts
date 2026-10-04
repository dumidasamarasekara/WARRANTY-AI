import { useQuery } from '@tanstack/react-query'
import type { StaffRole } from './roles'
import { useStaffSession } from './useStaffSession'

/** `Me` from contracts/rest-api.openapi.yaml. */
export interface Me {
  sub: string
  name: string
  tenantDisplayName: string
  tenantCurrency: string
  roles: StaffRole[]
}

class HttpError extends Error {
  readonly status: number

  constructor(status: number, path: string) {
    super(`GET ${path} failed with ${status}`)
    this.status = status
  }
}

async function getJson<T>(path: string, accessToken: string, signal: AbortSignal): Promise<T> {
  const response = await fetch(path, { headers: { Authorization: `Bearer ${accessToken}` }, signal })
  if (!response.ok) throw new HttpError(response.status, path)
  return (await response.json()) as T
}

/** The tenant (display name, currency) and roles of the signed-in staff user. */
export function useMe() {
  const session = useStaffSession()
  return useQuery({
    queryKey: ['me'],
    queryFn: ({ signal }) => getJson<Me>('/api/me', session!.accessToken, signal),
    enabled: session !== null,
    staleTime: 5 * 60_000,
  })
}

/** Number of escalated claims waiting in the reviewer's queue (sidebar count badge). */
export function useReviewQueueCount(enabled: boolean) {
  const session = useStaffSession()
  return useQuery({
    queryKey: ['review-queue'],
    queryFn: ({ signal }) => getJson<unknown[]>('/api/review-queue', session!.accessToken, signal),
    select: (items) => items.length,
    enabled: enabled && session !== null,
  })
}
