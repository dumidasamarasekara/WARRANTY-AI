import { useEffect, useId, useRef, useState } from 'react'
import { Link, NavLink, Outlet, useMatches } from 'react-router'
import { Breadcrumbs, EmptyState, cx, type BreadcrumbItem } from '../shared/ui'
import { hasAnyRole, staffRoleLabels, type StaffRole } from './roles'
import { crumbLabel } from './routeHandle'
import { useMe, useReviewQueueCount } from './staffQueries'
import { TenantBanner, TenantStrip } from './TenantBanner'
import { tenantMarkerColor } from './tenantTheme'
import { useStaffSession, type StaffSession } from './useStaffSession'
import styles from './Layout.module.css'

interface NavItem {
  to: string
  label: string
  initials: string
  /** `null` = every staff role. */
  roles: readonly StaffRole[] | null
  counter?: 'review-queue'
}

// ui-design.md §6.1. Users with several roles see the union of their items.
const navItems: readonly NavItem[] = [
  { to: '/staff/claims', label: 'Claims', initials: 'CL', roles: null },
  { to: '/staff/review', label: 'Review queue', initials: 'HR', roles: ['claims-reviewer'], counter: 'review-queue' },
  { to: '/staff/policies', label: 'Policies', initials: 'WP', roles: null },
  { to: '/staff/security-events', label: 'Security events', initials: 'SE', roles: ['auditor'] },
]

function initialsOf(name: string): string {
  return name
    .split(/[\s._-]+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part.charAt(0).toUpperCase())
    .join('')
}

/** Staff shell: role-filtered sidebar, tenant strip, header with breadcrumbs, tenant chip and user menu. */
export function Layout() {
  const session = useStaffSession()
  const me = useMe()
  const [collapsed, setCollapsed] = useState(false)
  const isReviewer = session !== null && session.roles.includes('claims-reviewer')
  const queueCount = useReviewQueueCount(isReviewer)

  if (!session) return null
  if (session.roles.length === 0) {
    return <EmptyState message="Your account has no WarrantyOS role. Ask an administrator for access." />
  }

  const items = navItems.filter((item) => item.roles === null || hasAnyRole(session.roles, item.roles))
  const tenantName = me.data?.tenantDisplayName

  return (
    <div className={cx(styles.shell, collapsed && styles.collapsed)}>
      <aside className={styles.sidebar}>
        <Link to="/staff" className={styles.brand} aria-label="WarrantyOS home">
          <span className={styles.brandMark} aria-hidden="true">
            W
          </span>
          <span className={styles.brandName}>WarrantyOS</span>
        </Link>
        <nav aria-label="Main" className={styles.nav}>
          <ul className={styles.navList}>
            {items.map((item) => {
              const count = item.counter === 'review-queue' ? queueCount.data : undefined
              return (
                <li key={item.to}>
                  <NavLink
                    to={item.to}
                    className={({ isActive }) => cx(styles.navItem, isActive && styles.navItemCurrent)}
                    title={item.label}
                  >
                    <span className={styles.navTile} aria-hidden="true">
                      {item.initials}
                    </span>
                    <span className={styles.navLabel}>{item.label}</span>
                    {count !== undefined && count > 0 && (
                      <span className={styles.navCount}>
                        {count}
                        <span className="visually-hidden"> waiting</span>
                      </span>
                    )}
                  </NavLink>
                </li>
              )
            })}
          </ul>
        </nav>
        <button
          type="button"
          className={styles.collapse}
          onClick={() => setCollapsed((value) => !value)}
          aria-pressed={collapsed}
          aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
        >
          <span aria-hidden="true">{collapsed ? '»' : '«'}</span>
          <span className={styles.navLabel}>Collapse</span>
        </button>
      </aside>

      <div className={styles.column}>
        <TenantStrip tenantName={tenantName} />
        <header className={styles.header}>
          <StaffBreadcrumbs tenantName={tenantName} />
          <div className={styles.headerActions}>
            <span className={styles.tenantChip} title="Your organisation (from your sign-in)">
              <span className={styles.tenantDot} style={{ background: tenantMarkerColor(tenantName) }} aria-hidden="true" />
              <span className="visually-hidden">Tenant: </span>
              {tenantName ?? (me.isError ? 'Tenant unavailable' : 'Loading…')}
            </span>
            <UserMenu session={session} />
          </div>
        </header>
        <TenantBanner area="staff" />
        <main className={styles.main}>
          <Outlet />
        </main>
      </div>
    </div>
  )
}

function StaffBreadcrumbs({ tenantName }: { tenantName: string | undefined }) {
  const matches = useMatches()
  const items: BreadcrumbItem[] = [{ label: tenantName ?? 'Tenant' }]
  for (const match of matches) {
    const label = crumbLabel(match.handle, match.params)
    if (label) items.push({ label, to: match.pathname })
  }
  return <Breadcrumbs items={items} link={Link} className={styles.breadcrumbs} />
}

function UserMenu({ session }: { session: StaffSession }) {
  const [open, setOpen] = useState(false)
  const containerRef = useRef<HTMLDivElement>(null)
  const buttonRef = useRef<HTMLButtonElement>(null)
  const menuId = useId()

  useEffect(() => {
    if (!open) return
    const onPointerDown = (event: PointerEvent) => {
      if (!containerRef.current?.contains(event.target as Node)) setOpen(false)
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setOpen(false)
        buttonRef.current?.focus()
      }
    }
    document.addEventListener('pointerdown', onPointerDown)
    document.addEventListener('keydown', onKeyDown)
    return () => {
      document.removeEventListener('pointerdown', onPointerDown)
      document.removeEventListener('keydown', onKeyDown)
    }
  }, [open])

  return (
    <div className={styles.userMenu} ref={containerRef}>
      <button
        ref={buttonRef}
        type="button"
        className={styles.userButton}
        aria-expanded={open}
        aria-controls={menuId}
        onClick={() => setOpen((value) => !value)}
      >
        <span className={styles.avatar} aria-hidden="true">
          {initialsOf(session.name)}
        </span>
        <span className={styles.userName}>{session.name}</span>
      </button>
      {open && (
        <div id={menuId} className={styles.menu}>
          <p className={styles.menuName}>{session.name}</p>
          <p className={styles.menuRoles}>{session.roles.map((role) => staffRoleLabels[role]).join(' · ')}</p>
          <button type="button" className={styles.menuItem} onClick={() => void session.signOut()}>
            Sign out
          </button>
        </div>
      )}
    </div>
  )
}
