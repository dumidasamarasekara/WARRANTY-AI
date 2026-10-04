import { useCallback, type ReactNode } from 'react'
import type { User } from 'oidc-client-ts'
import { AuthProvider as OidcAuthProvider } from 'react-oidc-context'
import { useNavigate } from 'react-router'

const authority = import.meta.env.VITE_KEYCLOAK_AUTHORITY ?? 'http://localhost:8080/realms/warranty'
const clientId = import.meta.env.VITE_KEYCLOAK_CLIENT_ID ?? 'warranty-web'

/** Where the staff area sends Keycloak back to; the callback then restores the requested page. */
const staffHome = '/staff'

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
 * Staff sign-in through Keycloak's hosted login: Authorization Code + PKCE (oidc-client-ts uses S256
 * by default), tokens kept in session storage for the tab. Sits inside the router so the sign-in
 * callback can navigate. The claimant area never starts a sign-in.
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
    <OidcAuthProvider
      authority={authority}
      client_id={clientId}
      redirect_uri={`${window.location.origin}${staffHome}`}
      post_logout_redirect_uri={`${window.location.origin}${staffHome}`}
      scope="openid profile email"
      onSigninCallback={onSigninCallback}
    >
      {children}
    </OidcAuthProvider>
  )
}
