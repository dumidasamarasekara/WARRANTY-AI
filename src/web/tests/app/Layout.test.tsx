import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { RouterProvider, createMemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Layout } from '../../src/app/Layout'
import { RequireRole } from '../../src/app/RequireRole'
import type { StaffRole } from '../../src/app/roles'
import { StaffLanding } from '../../src/app/ShellPages'

const signoutRedirect = vi.fn()
let roles: StaffRole[] = []

function accessToken(): string {
  const payload = btoa(JSON.stringify({ realm_access: { roles } }))
  return `header.${payload}.signature`
}

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    isLoading: false,
    user: { access_token: accessToken(), profile: { sub: 'u-1', name: 'Riley Reviewer' } },
    signoutRedirect,
  }),
  hasAuthParams: () => false,
}))

function renderShell(path: string) {
  const router = createMemoryRouter(
    [
      {
        path: '/staff',
        element: <Layout />,
        children: [
          { index: true, element: <StaffLanding /> },
          { path: 'claims', handle: { crumb: 'Claims' }, element: <h1>Claims page</h1> },
          { path: 'review', handle: { crumb: 'Review queue' }, element: <h1>Review page</h1> },
          {
            path: 'security-events',
            element: (
              <RequireRole roles={['auditor']}>
                <h1>Security events page</h1>
              </RequireRole>
            ),
          },
        ],
      },
    ],
    { initialEntries: [path] },
  )
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) =>
      new URL(request.url).pathname === '/api/me'
        ? Response.json({ sub: 'u-1', name: 'Riley Reviewer', tenantDisplayName: 'Aurora Electronics', tenantCurrency: 'USD', roles })
        : Response.json([{}, {}, {}]),
    ),
  )
})

afterEach(() => {
  vi.unstubAllGlobals()
  signoutRedirect.mockReset()
})

function navLabels(): string[] {
  const nav = screen.getByRole('navigation', { name: 'Main' })
  return within(nav)
    .getAllByRole('link')
    .map((link) => link.getAttribute('title') ?? '')
}

describe('staff Layout', () => {
  it('shows the union of navigation items for a user with several roles', async () => {
    roles = ['claims-agent', 'claims-reviewer']
    renderShell('/staff/claims')
    expect(navLabels()).toEqual(['Claims', 'Review queue', 'Policies'])
    expect(await screen.findByText('3')).toBeInTheDocument()
  })

  it('shows Security events to auditors and hides the review queue', () => {
    roles = ['auditor']
    renderShell('/staff/claims')
    expect(navLabels()).toEqual(['Claims', 'Policies', 'Security events'])
  })

  it('shows the tenant read-only from /api/me and breadcrumbs for the page', async () => {
    roles = ['claims-agent']
    renderShell('/staff/claims')
    const breadcrumbs = screen.getByRole('navigation', { name: 'Breadcrumb' })
    expect(await within(breadcrumbs).findByText('Aurora Electronics')).toBeInTheDocument()
    expect(screen.getByTitle('Your organisation (from your sign-in)')).toHaveTextContent('Aurora Electronics')
    expect(within(breadcrumbs).getByText('Claims')).toHaveAttribute('aria-current', 'page')
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
  })

  it('lands reviewers on the review queue and others on the claims list', async () => {
    roles = ['claims-reviewer']
    renderShell('/staff')
    expect(await screen.findByRole('heading', { name: 'Review page' })).toBeInTheDocument()
  })

  it('lands agents on the claims list', async () => {
    roles = ['claims-agent']
    renderShell('/staff')
    expect(await screen.findByRole('heading', { name: 'Claims page' })).toBeInTheDocument()
  })

  it('does not render a role-restricted page for other roles', () => {
    roles = ['claims-agent']
    renderShell('/staff/security-events')
    expect(screen.queryByRole('heading', { name: 'Security events page' })).not.toBeInTheDocument()
    expect(screen.getByText("You don't have access to this page.")).toBeInTheDocument()
  })

  it('signs out from the user menu', async () => {
    roles = ['claims-agent']
    renderShell('/staff/claims')
    await userEvent.click(screen.getByRole('button', { name: /Riley Reviewer/ }))
    expect(screen.getByText('Claims agent')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }))
    expect(signoutRedirect).toHaveBeenCalledOnce()
  })
})
