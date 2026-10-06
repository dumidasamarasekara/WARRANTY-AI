import { useQuery } from '@tanstack/react-query'
import { api, unwrap } from '../shared/api/client'

/** The tenant behind this channel host; the API resolves it from the Host header (research R9). */
export function usePublicTenant() {
  return useQuery({
    queryKey: ['public-tenant'],
    queryFn: ({ signal }) => unwrap(api.GET('/api/public/tenant', { signal })),
    staleTime: Infinity,
  })
}
