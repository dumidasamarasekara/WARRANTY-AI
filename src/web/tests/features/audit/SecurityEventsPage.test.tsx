import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { RouterProvider, createMemoryRouter } from 'react-router'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { SecurityEventsPage } from '../../../src/features/audit/SecurityEventsPage'

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    isLoading: false,
    user: {
      access_token: `header.${btoa(JSON.stringify({ realm_access: { roles: ['auditor'] } }))}.signature`,
      profile: { sub: 'u-3', name: 'Ari Auditor' },
    },
    signoutRedirect: vi.fn(),
  }),
  hasAuthParams: () => false,
}))

// Newest first, as GET /api/security-events returns them. The extra fields stand in for a server
// that wrongly leaked them: the page must never render them.
const events = [
  {
    id: 'aaaaaaaa-0000-0000-0000-000000000004',
    occurredAt: '2026-10-05T14:02:09Z',
    kind: 'SELF_REVIEW_REFUSED',
    actor: 'Agent Reviewer',
    target: 'WC-2026-000140',
    details: { reason: 'submitted-by-reviewer' },
    sourceIp: '10.0.0.7',
  },
  {
    id: 'aaaaaaaa-0000-0000-0000-000000000003',
    occurredAt: '2026-10-05T13:40:00Z',
    kind: 'CLAIMANT_ACCESS_FAILED',
    actor: 'claimant channel',
    target: 'WC-2026-000101',
  },
  {
    id: 'aaaaaaaa-0000-0000-0000-000000000002',
    occurredAt: '2026-10-05T13:20:00Z',
    kind: 'ACCESS_DENIED',
    actor: 'Riley Reviewer',
    target: '99999999-9999-9999-9999-999999999999',
  },
  {
    id: 'aaaaaaaa-0000-0000-0000-000000000001',
    occurredAt: '2026-10-05T13:10:00Z',
    kind: 'TOOL_SCOPE_VIOLATION',
    actor: 'adjudication-service',
  },
]

let requests: URL[] = []
let total = 54

const server = setupServer(
  http.get('*/api/me', () =>
    HttpResponse.json({ sub: 'u-3', name: 'Ari Auditor', tenantDisplayName: 'Aurora Electronics', tenantCurrency: 'USD', roles: ['auditor'] }),
  ),
  http.get('*/api/security-events', ({ request }) => {
    const url = new URL(request.url)
    requests.push(url)
    const page = Number(url.searchParams.get('page') ?? 1)
    const kind = url.searchParams.get('kind')
    const items = page === 1 ? events.filter((event) => !kind || event.kind === kind) : []
    return HttpResponse.json({ items, page, pageSize: 50, total: kind ? items.length : total })
  }),
)

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
afterEach(() => {
  server.resetHandlers()
  requests = []
  total = 54
})
afterAll(() => server.close())

function renderPage() {
  const router = createMemoryRouter([{ path: '/staff/security-events', element: <SecurityEventsPage /> }], {
    initialEntries: ['/staff/security-events'],
  })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return userEvent.setup()
}

const table = () => screen.getByRole('table', { name: 'Security events' })

describe('SecurityEventsPage', () => {
  it('lists the events newest first with each kind as a labelled badge, actor and target', async () => {
    renderPage()

    expect(await screen.findByRole('heading', { name: 'Security events — Aurora Electronics' })).toBeInTheDocument()
    expect(screen.getByText(/Entries cannot be changed or deleted\./)).toBeInTheDocument()
    await screen.findByText('WC-2026-000140')
    const rows = within(table()).getAllByRole('row')
    expect(within(rows[0]!).getAllByRole('columnheader').map((cell) => cell.textContent)).toEqual(['Time', 'Kind', 'Actor', 'Target'])
    expect(rows.slice(1).map((row) => within(row).getAllByRole('cell')[1]!.textContent)).toEqual([
      'Self-review refused',
      'Claimant access failed',
      'Access denied',
      'AI scope violation',
    ])
    expect(rows[1]).toHaveTextContent('Agent Reviewer')
    expect(rows[2]).toHaveTextContent('claimant channel')
    expect(rows[3]).toHaveTextContent('99999999-9999-9999-9999-999999999999')
    expect(rows[4]).toHaveTextContent('adjudication-service')
    expect(within(rows[1]!).getByRole('time')).toHaveAttribute('dateTime', '2026-10-05T14:02:09Z')
    expect(rows[1]).toHaveTextContent('05 Oct 2026')
  })

  it('never shows event details or source IP addresses', async () => {
    renderPage()

    await screen.findByText('WC-2026-000140')
    expect(document.body).not.toHaveTextContent('10.0.0.7')
    expect(document.body).not.toHaveTextContent('submitted-by-reviewer')
    expect(screen.queryByRole('columnheader', { name: /details|ip/i })).not.toBeInTheDocument()
    expect(within(table()).queryByRole('button')).not.toBeInTheDocument()
    expect(within(table()).queryByRole('link')).not.toBeInTheDocument()
  })

  it('filters by kind and pages through the results', async () => {
    const user = renderPage()
    await screen.findByText('Showing 1–50 of 54')
    expect(requests.at(-1)?.searchParams.get('kind')).toBeNull()

    await user.click(screen.getByRole('button', { name: 'Next' }))
    await waitFor(() => expect(requests.at(-1)?.searchParams.get('page')).toBe('2'))

    await user.selectOptions(screen.getByLabelText('Kind'), 'Access denied')
    await waitFor(() => expect(requests.at(-1)?.searchParams.get('kind')).toBe('ACCESS_DENIED'))
    // Changing the filter starts again from the first page.
    expect(requests.at(-1)?.searchParams.get('page')).toBe('1')
    const rows = await waitFor(() => {
      const current = within(table()).getAllByRole('row')
      expect(current).toHaveLength(2)
      return current
    })
    expect(rows[1]).toHaveTextContent('Access denied')

    await user.selectOptions(screen.getByLabelText('Kind'), 'AI scope violation (retrieval)')
    await waitFor(() => expect(requests.at(-1)?.searchParams.get('kind')).toBe('RETRIEVAL_SCOPE_VIOLATION'))
    expect(await screen.findByText('No security events of this kind.')).toBeInTheDocument()
  })

  it('shows the empty state when nothing has been recorded', async () => {
    server.use(http.get('*/api/security-events', () => HttpResponse.json({ items: [], page: 1, pageSize: 50, total: 0 })))
    renderPage()

    expect(await screen.findByText('No security events recorded')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Next' })).not.toBeInTheDocument()
  })

  it('shows the problem when the API refuses the request', async () => {
    server.use(
      http.get('*/api/security-events', () =>
        HttpResponse.json(
          { title: 'Forbidden', status: 403, detail: 'Auditor role required.', correlationId: 'corr-403' },
          { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    )
    renderPage()

    expect(await screen.findByRole('alert')).toHaveTextContent('Forbidden')
  })
})
