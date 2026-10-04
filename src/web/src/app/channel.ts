const defaultStaffHosts = ['localhost', '127.0.0.1']

function configuredStaffHosts(): string[] {
  const configured = import.meta.env.VITE_STAFF_HOSTS
  if (!configured) return defaultStaffHosts
  return configured
    .split(',')
    .map((host) => host.trim().toLowerCase())
    .filter(Boolean)
}

/**
 * Claimant channels are their own hosts (`aurora.localhost`, `borealis.localhost`). This only picks
 * which area the SPA renders: the API alone maps the Host header to a tenant (research R9) and
 * answers 404 from `GET /api/public/tenant` for a host that is not a channel.
 */
export function isTenantChannelHost(hostname: string, staffHosts: readonly string[] = configuredStaffHosts()): boolean {
  return !staffHosts.includes(hostname.toLowerCase())
}
