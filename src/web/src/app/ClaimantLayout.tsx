import { Outlet } from 'react-router'
import { LoadingState, ProblemState } from '../shared/ui'
import { usePublicTenant } from './publicQueries'
import { TenantBanner } from './TenantBanner'
import styles from './ClaimantLayout.module.css'

/** Claimant portal shell: tenant-branded header, no account (ui-design.md §6.6). */
export function ClaimantLayout() {
  const tenant = usePublicTenant()

  return (
    <div className={styles.portal}>
      <TenantBanner area="claimant" />
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
