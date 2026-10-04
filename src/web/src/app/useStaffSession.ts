import { useMemo } from 'react'
import { useAuth } from 'react-oidc-context'
import { rolesFromAccessToken, type StaffRole } from './roles'

export interface StaffSession {
  /** Display name from the ID token (falls back to the username). */
  name: string
  roles: StaffRole[]
  accessToken: string
  signOut: () => Promise<void>
}

/** The signed-in staff user, or `null` before sign-in completes. */
export function useStaffSession(): StaffSession | null {
  const auth = useAuth()
  const user = auth.user

  return useMemo(() => {
    if (!auth.isAuthenticated || !user) return null
    return {
      name: user.profile.name ?? user.profile.preferred_username ?? user.profile.sub,
      roles: rolesFromAccessToken(user.access_token),
      accessToken: user.access_token,
      signOut: () => auth.signoutRedirect(),
    }
  }, [auth, user])
}
