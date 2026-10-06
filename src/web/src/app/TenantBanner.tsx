import { usePublicTenant } from './publicQueries'
import { staffRoleLabels, staffRoles, type StaffRole } from './roles'
import { useMe } from './staffQueries'
import { tenantInitials, tenantMarkerColor } from './tenantTheme'
import styles from './TenantBanner.module.css'

/** Only the platform's staff roles, in their usual order; anything else in `/api/me` is not shown. */
function staffRolesOf(roles: readonly string[]): StaffRole[] {
  return staffRoles.filter((role) => roles.includes(role))
}

/** 3 px bar in the tenant marker colour at the top of the staff content column (ui-design.md §6.1). */
export function TenantStrip({ tenantName }: { tenantName: string | undefined }) {
  return <div className={styles.strip} style={{ background: tenantMarkerColor(tenantName) }} aria-hidden="true" />
}

export type TenantBannerProps = { area: 'staff' } | { area: 'claimant' }

/**
 * Who the user acts for, read-only: the staff context bar with the tenant and roles from `GET
 * /api/me` (ui-design.md §6.1), or the claimant portal's branded header with the display name from
 * `GET /api/public/tenant` (§6.6). Both come from the API — the token or the channel Host — never
 * from a choice made in the SPA.
 */
export function TenantBanner(props: TenantBannerProps) {
  return props.area === 'staff' ? <StaffContextBar /> : <ClaimantHeader />
}

function StaffContextBar() {
  const me = useMe()
  const tenantName = me.data?.tenantDisplayName ?? (me.isError ? 'Unavailable' : 'Loading…')
  const roles = me.data ? staffRolesOf(me.data.roles) : []

  return (
    <div className={styles.contextBar} role="region" aria-label="Tenant context">
      <span>
        <span className={styles.label}>Tenant</span> <b className={styles.value}>{tenantName}</b>
      </span>
      {roles.length > 0 && (
        <span>
          <span className={styles.label}>{roles.length === 1 ? 'Role' : 'Roles'}</span>{' '}
          <b className={styles.value}>{roles.map((role) => staffRoleLabels[role]).join(' · ')}</b>
        </span>
      )}
      {me.data && (
        <span>
          <span className={styles.label}>Currency</span> <b className={styles.value}>{me.data.tenantCurrency}</b>
        </span>
      )}
      <span>
        <span className={styles.label}>Environment</span> <b className={styles.value}>Local PoC</b>
      </span>
      <span className={styles.isolation}>Data isolated to this tenant</span>
    </div>
  )
}

function ClaimantHeader() {
  const tenant = usePublicTenant()
  const displayName = tenant.data?.displayName

  return (
    <header className={styles.claimantHeader}>
      <span className={styles.tile} style={{ background: tenantMarkerColor(displayName) }} aria-hidden="true">
        {tenantInitials(displayName)}
      </span>
      <span className={styles.tenantName}>{displayName ?? ' '}</span>
      <span className={styles.product}>Warranty claims</span>
    </header>
  )
}
