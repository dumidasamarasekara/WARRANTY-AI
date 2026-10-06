import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { TenantBanner, type TenantBannerProps } from '../../src/app/TenantBanner'

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    isLoading: false,
    user: { access_token: 'header.e30.signature', profile: { sub: 'u-1', name: 'Riley Reviewer' } },
    signoutRedirect: vi.fn(),
  }),
  hasAuthParams: () => false,
}))

const server = setupServer()

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
afterEach(() => server.resetHandlers())
afterAll(() => server.close())

function renderBanner(props: TenantBannerProps) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <TenantBanner {...props} />
    </QueryClientProvider>,
  )
}

function respondWithMe(me: Record<string, unknown>) {
  server.use(
    http.get('*/api/me', () =>
      HttpResponse.json({ sub: 'u-1', name: 'riley', tenantDisplayName: 'Aurora Electronics', tenantCurrency: 'USD', roles: [], ...me }),
    ),
  )
}

describe('TenantBanner (staff)', () => {
  it('shows the tenant, roles and currency from /api/me with the isolation note', async () => {
    respondWithMe({ roles: ['claims-agent', 'claims-reviewer'] })
    renderBanner({ area: 'staff' })

    const bar = screen.getByRole('region', { name: 'Tenant context' })
    expect(await screen.findByText('Aurora Electronics')).toBeInTheDocument()
    expect(bar).toHaveTextContent('Roles Claims agent · Claims reviewer')
    expect(bar).toHaveTextContent('Currency USD')
    expect(bar).toHaveTextContent('Environment Local PoC')
    expect(bar).toHaveTextContent('Data isolated to this tenant')
  })

  it('names a single role in the singular', async () => {
    respondWithMe({ tenantDisplayName: 'Borealis Devices', tenantCurrency: 'EUR', roles: ['auditor'] })
    renderBanner({ area: 'staff' })

    expect(await screen.findByText('Borealis Devices')).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Tenant context' })).toHaveTextContent('Role Auditor')
  })

  it('offers no way to choose another tenant and says when the tenant cannot be loaded', async () => {
    server.use(http.get('*/api/me', () => HttpResponse.json({ status: 500, title: 'Server error' }, { status: 500 })))
    renderBanner({ area: 'staff' })

    expect(await screen.findByText('Unavailable')).toBeInTheDocument()
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })
})

describe('TenantBanner (claimant)', () => {
  it('brands the header with the channel tenant from /api/public/tenant and no account', async () => {
    server.use(http.get('*/api/public/tenant', () => HttpResponse.json({ displayName: 'Aurora Electronics' })))
    renderBanner({ area: 'claimant' })

    const header = screen.getByRole('banner')
    expect(await screen.findByText('Aurora Electronics')).toBeInTheDocument()
    expect(header).toHaveTextContent('AE')
    expect(header).toHaveTextContent('Warranty claims')
    expect(header).not.toHaveTextContent('Riley Reviewer')
  })
})
