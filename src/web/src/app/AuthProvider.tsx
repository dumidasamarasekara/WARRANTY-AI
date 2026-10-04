import { useCallback, type ReactNode } from 'react'
import type { User } from 'oidc-client-ts'
import { AuthProvider as OidcAuthProvider } from 'react-oidc-context'
import { useNavigate } from 'react-router'
import { staffHome, userManager } from './oidc'

/** Carried through the sign-in round trip in the OIDC `state`. */
export interface SignInState {
  returnTo: string
}

function returnPath(state: unknown): string {
  const returnTo = (state as Partial<SignInState> | undefined)?.returnTo
  // Only same-origin paths: never follow a full URL out of the app.
  return typeof returnTo === 'string' && returnTo.startsWith('/') && !returnTo.startsWith('//') ? returnTo : staffHome
}

/**
 * Staff sign-in (see `oidc.ts`). Sits inside the router so the sign-in callback can navigate back to
 * the requested page. The claimant area never starts a sign-in.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const navigate = useNavigate()

  const onSigninCallback = useCallback(
    (user: User | undefined) => {
      void navigate(returnPath(user?.state), { replace: true })
    },
    [navigate],
  )

  return (
    <OidcAuthProvider userManager={userManager} onSigninCallback={onSigninCallback}>
      {children}
    </OidcAuthProvider>
  )
}
