export const staffRoles = ['claims-agent', 'claims-reviewer', 'auditor'] as const

export type StaffRole = (typeof staffRoles)[number]

export const staffRoleLabels: Record<StaffRole, string> = {
  'claims-agent': 'Claims agent',
  'claims-reviewer': 'Claims reviewer',
  auditor: 'Auditor',
}

function decodeJwtPayload(token: string): unknown {
  const payload = token.split('.')[1]
  if (!payload) return null
  try {
    const base64 = payload.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(payload.length / 4) * 4, '=')
    const bytes = Uint8Array.from(atob(base64), (char) => char.charCodeAt(0))
    return JSON.parse(new TextDecoder().decode(bytes))
  } catch {
    return null
  }
}

/**
 * The staff roles in a Keycloak access token (`realm_access.roles`). The SPA reads them only to
 * choose what to render; the API authorizes every request from the validated token itself.
 */
export function rolesFromAccessToken(accessToken: string | undefined): StaffRole[] {
  if (!accessToken) return []
  const payload = decodeJwtPayload(accessToken) as { realm_access?: { roles?: unknown } } | null
  const roles = payload?.realm_access?.roles
  return Array.isArray(roles) ? staffRoles.filter((role) => roles.includes(role)) : []
}

export function hasAnyRole(granted: readonly StaffRole[], required: readonly StaffRole[]): boolean {
  return required.some((role) => granted.includes(role))
}
