import { Navigate, Outlet, createBrowserRouter, type RouteObject } from 'react-router'
import { AuthProvider } from './AuthProvider'
import { ClaimantLayout } from './ClaimantLayout'
import { Layout } from './Layout'
import { RequireRole, RequireSignIn } from './RequireRole'
import type { RouteHandle } from './routeHandle'
import { NotFoundPage, PlaceholderPage, StaffLanding } from './ShellPages'

const crumb = (value: RouteHandle['crumb']): RouteHandle => ({ crumb: value })

/** Staff workspace under `/staff`, signed in through Keycloak; reachable from any host. */
const staffRoutes: RouteObject = {
  path: '/staff',
  element: (
    <RequireSignIn>
      <Layout />
    </RequireSignIn>
  ),
  children: [
    { index: true, element: <StaffLanding /> },
    {
      path: 'claims',
      handle: crumb('Claims'),
      children: [
        { index: true, element: <PlaceholderPage title="Claims" /> },
        {
          path: 'new',
          handle: crumb('New claim'),
          element: (
            <RequireRole roles={['claims-agent']}>
              <PlaceholderPage title="New claim" />
            </RequireRole>
          ),
        },
        {
          path: ':claimId/*',
          handle: crumb((params) => params.claimId ?? 'Claim'),
          element: <PlaceholderPage title="Claim" />,
        },
      ],
    },
    {
      path: 'review/:claimId?',
      handle: crumb('Review queue'),
      element: (
        <RequireRole roles={['claims-reviewer']}>
          <PlaceholderPage title="Review queue" />
        </RequireRole>
      ),
    },
    { path: 'policies', handle: crumb('Policies'), element: <PlaceholderPage title="Warranty policies" /> },
    {
      path: 'security-events',
      handle: crumb('Security events'),
      element: (
        <RequireRole roles={['auditor']}>
          <PlaceholderPage title="Security events" />
        </RequireRole>
      ),
    },
    { path: '*', element: <NotFoundPage home="/staff" /> },
  ],
}

/** Claimant portal on a tenant channel host; claimants have no account (spec, research R10). */
const claimantRoutes: RouteObject = {
  element: <ClaimantLayout />,
  children: [
    { index: true, element: <PlaceholderPage title="Submit a warranty claim" /> },
    { path: 'claims/access', element: <PlaceholderPage title="Check your claim" /> },
    { path: 'claims/:reference', element: <PlaceholderPage title="Your claim" /> },
    { path: '*', element: <NotFoundPage home="/" /> },
  ],
}

export function createRoutes(isTenantChannel: boolean): RouteObject[] {
  return [
    {
      // Inside the router so the OIDC sign-in callback can navigate back to the requested page.
      element: (
        <AuthProvider>
          <Outlet />
        </AuthProvider>
      ),
      children: isTenantChannel
        ? [staffRoutes, claimantRoutes]
        : [staffRoutes, { index: true, element: <Navigate to="/staff" replace /> }, { path: '*', element: <NotFoundPage home="/staff" /> }],
    },
  ]
}

export function createAppRouter(isTenantChannel: boolean) {
  return createBrowserRouter(createRoutes(isTenantChannel))
}
