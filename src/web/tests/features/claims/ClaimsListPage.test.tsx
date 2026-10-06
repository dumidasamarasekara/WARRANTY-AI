import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { RouterProvider, createMemoryRouter } from 'react-router'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { ClaimsListPage } from '../../../src/features/claims/ClaimsListPage'

const auth = vi.hoisted(() => ({ roles: ['claims-agent'] as string[] }))

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    isLoading: false,
    user: {
      access_token: `header.${btoa(JSON.stringify({ realm_access: { roles: auth.roles } }))}.signature`,
      profile: { sub: 'u-1', name: 'Avery Agent' },
    },
    signoutRedirect: vi.fn(),
  }),
  hasAuthParams: () => false,
}))

const claims = [
  {
    claimId: '11111111-1111-1111-1111-111111111111',
    reference: 'WC-2026-000101',
    status: 'UnderReview',
    productName: 'Aurora Tab 11',
    submittedAt: '2026-10-02T09:41:00Z',
    aiDecision: 'APPROVE',
    disposition: 'HumanReview',
  },
  {
    claimId: '22222222-2222-2222-2222-222222222222',
    reference: 'WC-2026-000102',
    status: 'Submitted',
    productName: 'AUR-OVEN-60',
    submittedAt: '2026-10-03T10:00:00Z',
  },
]

let requests: URL[] = []

const server = setupServer(
  http.get('*/api/me', () =>
    HttpResponse.json({ sub: 'u-1', name: 'Avery Agent', tenantDisplayName: 'Aurora Electronics', tenantCurrency: 'USD', roles: auth.roles }),
  ),
  http.get('*/api/claims', ({ request }) => {
    const url = new URL(request.url)
    requests.push(url)
    const page = Number(url.searchParams.get('page') ?? 1)
    return HttpResponse.json({ items: page === 1 ? claims : [], page, pageSize: 25, total: 27 })
  }),
)

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
afterEach(() => {
  server.resetHandlers()
  requests = []
})
afterAll(() => server.close())

function renderPage(roles: string[]) {
  auth.roles = roles
  const router = createMemoryRouter(
    [
      { path: '/staff/claims', element: <ClaimsListPage /> },
      { path: '/staff/claims/:claimId/*', element: <p>Claim workspace</p> },
    ],
    { initialEntries: ['/staff/claims'] },
  )
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return userEvent.setup()
}

const table = () => screen.getByRole('table', { name: 'Claims' })

describe('ClaimsListPage', () => {
  it('lists the claims with status, AI decision and outcome route, and offers New claim to agents', async () => {
    renderPage(['claims-agent'])

    expect(await screen.findByText('27 claims · Aurora Electronics')).toBeInTheDocument()
    const rows = within(table()).getAllByRole('row')
    expect(within(rows[0]!).getAllByRole('columnheader').map((cell) => cell.textContent)).toEqual([
      'Reference',
      'Product',
      'Status',
      'AI decision',
      'Outcome route',
      'Submitted',
    ])
    const first = within(rows[1]!)
    expect(first.getByRole('link', { name: 'WC-2026-000101' })).toHaveAttribute('href', '/staff/claims/11111111-1111-1111-1111-111111111111')
    expect(rows[1]).toHaveTextContent('Under review')
    expect(rows[1]).toHaveTextContent('AIApprove')
    expect(rows[1]).toHaveTextContent('Escalated to a person')
    expect(rows[1]).toHaveTextContent('02 Oct 2026')
    expect(rows[2]).toHaveTextContent('Submitted')
    expect(screen.getByRole('link', { name: 'New claim' })).toHaveAttribute('href', '/staff/claims/new')
    expect(screen.queryByRole('link', { name: /Decision trace/ })).not.toBeInTheDocument()
  })

  it('gives reviewers a trace link per claim and no New claim action', async () => {
    renderPage(['claims-reviewer'])

    expect(await screen.findByRole('link', { name: 'Decision trace of WC-2026-000101' })).toHaveAttribute(
      'href',
      '/staff/claims/11111111-1111-1111-1111-111111111111/trace',
    )
    expect(screen.queryByRole('link', { name: 'New claim' })).not.toBeInTheDocument()
  })

  it('gives auditors a read-only list with a trace link per claim', async () => {
    renderPage(['auditor'])

    expect(await screen.findByRole('link', { name: 'Decision trace of WC-2026-000102' })).toHaveAttribute(
      'href',
      '/staff/claims/22222222-2222-2222-2222-222222222222/trace',
    )
    expect(within(table()).getAllByRole('link', { name: /^Decision trace of/ })).toHaveLength(2)
    expect(screen.queryByRole('link', { name: 'New claim' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /review/i })).not.toBeInTheDocument()
  })

  it('filters by status and pages through the results', async () => {
    const user = renderPage(['auditor'])
    await screen.findByText('Showing 1–25 of 27')

    await user.selectOptions(screen.getByLabelText('Status'), 'Under review')
    await waitFor(() => expect(requests.at(-1)?.searchParams.get('status')).toBe('UnderReview'))

    await user.click(screen.getByRole('button', { name: 'Next' }))
    await waitFor(() => expect(requests.at(-1)?.searchParams.get('page')).toBe('2'))
    expect(requests.at(-1)?.searchParams.get('status')).toBe('UnderReview')
    expect(await screen.findByText('No claims with this status.')).toBeInTheDocument()
  })
})
