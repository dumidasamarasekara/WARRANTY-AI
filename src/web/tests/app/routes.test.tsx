import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import type { ReactNode } from 'react'
import { RouterProvider, createMemoryRouter } from 'react-router'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { createRoutes } from '../../src/app/routes'

const auth = vi.hoisted(() => ({ roles: ['auditor'] as string[] }))

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    isLoading: false,
    user: {
      access_token: `header.${btoa(JSON.stringify({ realm_access: { roles: auth.roles } }))}.signature`,
      profile: { sub: 'u-1', name: 'Alex Auditor' },
    },
    signoutRedirect: vi.fn(),
  }),
  hasAuthParams: () => false,
}))

// The real provider talks to Keycloak; the mocked `useAuth` above stands in for a signed-in session.
vi.mock('../../src/app/AuthProvider', () => ({
  AuthProvider: ({ children }: { children: ReactNode }) => children,
}))

const claimId = '11111111-1111-1111-1111-111111111111'

const server = setupServer(
  http.get('*/api/me', () =>
    HttpResponse.json({ sub: 'u-1', name: 'Alex Auditor', tenantDisplayName: 'Aurora Electronics', tenantCurrency: 'USD', roles: auth.roles }),
  ),
  http.get('*/api/claims', () =>
    HttpResponse.json({
      items: [
        {
          claimId,
          reference: 'WC-2026-000101',
          status: 'UnderReview',
          productName: 'Aurora Tab 11',
          submittedAt: '2026-10-02T09:41:00Z',
          aiDecision: 'APPROVE',
          disposition: 'HumanReview',
        },
      ],
      page: 1,
      pageSize: 25,
      total: 1,
    }),
  ),
)

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
afterEach(() => server.resetHandlers())
afterAll(() => server.close())

function renderApp(path: string, roles: string[]) {
  auth.roles = roles
  const router = createMemoryRouter(createRoutes(false), { initialEntries: [path] })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return router
}

const navLabels = () =>
  within(screen.getByRole('navigation', { name: 'Main' }))
    .getAllByRole('link')
    .map((link) => link.getAttribute('title'))

// ui-design.md §6.1–§6.2: auditors get the read-only claims list with trace links and never a review action.
describe('auditor navigation', () => {
  it('lands auditors on the claims list with trace links and no review or create actions', async () => {
    const router = renderApp('/staff', ['auditor'])

    expect(await screen.findByRole('link', { name: 'Decision trace of WC-2026-000101' })).toHaveAttribute(
      'href',
      `/staff/claims/${claimId}/trace`,
    )
    expect(router.state.location.pathname).toBe('/staff/claims')
    expect(navLabels()).toEqual(['Claims', 'Policies', 'Security events'])
    expect(screen.queryByRole('link', { name: 'New claim' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /review/i })).not.toBeInTheDocument()
  })

  it('does not render the review queue for auditors', async () => {
    renderApp(`/staff/review/${claimId}`, ['auditor'])

    expect(await screen.findByText("You don't have access to this page.")).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /approve|reject|override|request information/i })).not.toBeInTheDocument()
  })

  it('does not render the new claim form for auditors', async () => {
    renderApp('/staff/claims/new', ['auditor'])

    expect(await screen.findByText("You don't have access to this page.")).toBeInTheDocument()
  })
})
