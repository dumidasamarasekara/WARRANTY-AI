/**
 * Tenant marker colour for the tenant strip, tenant dot and claimant portal tile (ui-design.md §3.1).
 * Presentation only: it never decides which tenant the user acts for — that comes from the token.
 */
const tenantMarkers: Record<string, string> = {
  'Aurora Electronics': 'var(--color-tenant-aurora)',
  'Borealis Devices': 'var(--color-tenant-borealis)',
}

export const unknownTenantMarker = 'var(--color-tenant-unknown)'

export function tenantMarkerColor(displayName: string | null | undefined): string {
  return (displayName && tenantMarkers[displayName.trim()]) || unknownTenantMarker
}

/** Up to two initials for the claimant header tile ("Aurora Electronics" → "AE"). */
export function tenantInitials(displayName: string | null | undefined): string {
  return (displayName ?? '')
    .trim()
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((word) => word.charAt(0).toUpperCase())
    .join('')
}
