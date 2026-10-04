import { useEffect, type ReactNode } from 'react'
import { hasAuthParams, useAuth } from 'react-oidc-context'
import { Outlet, useLocation } from 'react-router'
import { Button, EmptyState, LoadingState, ProblemState } from '../shared/ui'
import type { SignInState } from './AuthProvider'
import { hasAnyRole, type StaffRole } from './roles'
import { useStaffSession } from './useStaffSession'

/** Starts Keycloak sign-in for staff pages and renders them once a session exists. */
export function RequireSignIn({ children }: { children?: ReactNode }) {
  const auth = useAuth()
  const location = useLocation()
  const signedOut = !auth.isAuthenticated && !auth.isLoading && !auth.activeNavigator && !auth.error && !hasAuthParams()

  useEffect(() => {
    if (!signedOut) return
    const state: SignInState = { returnTo: `${location.pathname}${location.search}` }
    void auth.signinRedirect({ state })
  }, [signedOut, auth, location.pathname, location.search])

  if (auth.error) {
    return (
      <ProblemState
        problem={{ title: 'Sign-in failed', detail: auth.error.message }}
        action={
          <Button variant="primary" onClick={() => void auth.signinRedirect()}>
            Sign in again
          </Button>
        }
      />
    )
  }
  if (!auth.isAuthenticated) return <LoadingState message="Signing you in…" />
  return children ?? <Outlet />
}

/**
 * Renders its content only for a user holding at least one of `roles`. This shapes the UI; the API
 * enforces the same role policies on every request.
 */
export function RequireRole({ roles, children }: { roles: readonly StaffRole[]; children?: ReactNode }) {
  const session = useStaffSession()
  if (!session) return null
  if (!hasAnyRole(session.roles, roles)) {
    return <EmptyState message="You don't have access to this page." />
  }
  return children ?? <Outlet />
}
