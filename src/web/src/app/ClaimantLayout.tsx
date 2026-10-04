import { useQuery } from '@tanstack/react-query'
import { Outlet } from 'react-router'
import { LoadingState, ProblemState } from '../shared/ui'
import { tenantInitials, tenantMarkerColor } from './tenantTheme'
import styles from './ClaimantLayout.module.css'

interface PublicTenantInfo {
  displayName: string
}

/** The tenant behind this channel host; the API resolves it from the Host header (research R9). */
function usePublicTenant() {
  return useQuery({
    queryKey: ['public-tenant'],
    queryFn: async ({ signal }) => {
      const response = await fetch('/api/public/tenant', { signal })
      if (!response.ok) throw Object.assign(new Error('Tenant not resolved'), { status: response.status })
      return (await response.json()) as PublicTenantInfo
    },
    staleTime: Infinity,
  })
}

/** Claimant portal shell: tenant-branded header, no account (ui-design.md §6.6). */
export function ClaimantLayout() {
  const tenant = usePublicTenant()
  const displayName = tenant.data?.displayName

  return (
    <div className={styles.portal}>
      <header className={styles.header}>
        <span className={styles.tile} style={{ background: tenantMarkerColor(displayName) }} aria-hidden="true">
          {tenantInitials(displayName)}
        </span>
        <span className={styles.tenantName}>{displayName ?? ' '}</span>
        <span className={styles.product}>Warranty claims</span>
      </header>
      <main className={styles.main}>
        {tenant.isPending ? (
          <LoadingState />
        ) : tenant.isError ? (
          <ProblemState
            problem={{
              title: 'This address is not a claims channel',
              detail: 'Use the warranty claims link from your retailer or manufacturer.',
            }}
          />
        ) : (
          <Outlet />
        )}
      </main>
    </div>
  )
}
