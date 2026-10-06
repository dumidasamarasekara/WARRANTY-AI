import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { PoliciesPage } from '../../../src/features/claims/PoliciesPage'
import {
  buildTimeline,
  policyVersionStatus,
  timelineHorizon,
  type PolicyVersionSummary,
  type TimelineSegment,
} from '../../../src/features/claims/policyVersions'

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    isLoading: false,
    user: { access_token: 'header.e30.signature', profile: { sub: 'u-1', name: 'Avery Agent' } },
    signoutRedirect: vi.fn(),
  }),
  hasAuthParams: () => false,
}))

const today = '2026-10-06'
const v1: PolicyVersionSummary = {
  policyCode: 'AUR-WP',
  title: 'Aurora Limited Warranty',
  version: 1,
  effectiveFrom: '2025-01-01',
  effectiveTo: '2026-06-30',
  regions: ['NA', 'EU'],
}
const v2: PolicyVersionSummary = { ...v1, version: 2, effectiveFrom: '2026-07-01', effectiveTo: null }
const v3: PolicyVersionSummary = { ...v1, version: 3, effectiveFrom: '2027-01-01', effectiveTo: null, regions: ['EU'] }

const server = setupServer(
  http.get('*/api/me', () =>
    HttpResponse.json({ sub: 'u-1', name: 'Avery Agent', tenantDisplayName: 'Aurora Electronics', tenantCurrency: 'USD', roles: ['claims-agent'] }),
  ),
)

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
afterEach(() => server.resetHandlers())
afterAll(() => server.close())

function respondWith(body: unknown, status = 200) {
  server.use(http.get('*/api/policies', () => HttpResponse.json(body as object, { status })))
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <PoliciesPage today={today} />
    </QueryClientProvider>,
  )
}

const versionButton = (name: RegExp) => within(screen.getByRole('navigation', { name: 'Policy versions' })).getByRole('button', { name })

describe('policy version status and timeline', () => {
  it('computes Active, Superseded and Scheduled from today with inclusive bounds', () => {
    expect(policyVersionStatus(v1, today)).toBe('Superseded')
    expect(policyVersionStatus(v1, '2026-06-30')).toBe('Active')
    expect(policyVersionStatus(v2, today)).toBe('Active')
    expect(policyVersionStatus(v2, '2026-06-30')).toBe('Scheduled')
  })

  it('sizes segments in proportion to their ranges and runs open-ended versions to today + 6 months', () => {
    expect(timelineHorizon(today)).toBe('2027-04-06')
    const timeline = buildTimeline([v1, v2], today)!

    expect(timeline.from).toBe('2025-01-01')
    expect(timeline.to).toBe('2027-04-06')
    const [first, second] = timeline.segments as [TimelineSegment, TimelineSegment]
    expect(first.start).toBe(0)
    expect(first.width + second.width).toBeCloseTo(100)
    expect(second.start).toBeCloseTo(first.width)
    expect(second.openEnded).toBe(true)
    expect(first.width).toBeGreaterThan(second.width)
  })
})

describe('PoliciesPage', () => {
  it('lists the tenant policy versions with computed status and shows the active one by default', async () => {
    respondWith([v1, v2, v3])
    renderPage()

    expect(await screen.findByRole('heading', { level: 1, name: 'Warranty policies — Aurora Electronics' })).toBeInTheDocument()
    expect(versionButton(/Policy v1/)).toHaveTextContent('Superseded')
    expect(versionButton(/Policy v1/)).toHaveTextContent('01 Jan 2025 – 30 Jun 2026')
    expect(versionButton(/Policy v2/)).toHaveTextContent('Active')
    expect(versionButton(/Policy v2/)).toHaveTextContent('01 Jul 2026 – open-ended')
    expect(versionButton(/Policy v3/)).toHaveTextContent('Scheduled')
    expect(versionButton(/Policy v2/)).toHaveAttribute('aria-pressed', 'true')

    expect(screen.getByRole('heading', { name: 'Aurora Limited Warranty · v2' })).toBeInTheDocument()
    expect(within(screen.getByRole('list', { name: 'Applicability timeline' })).getAllByRole('listitem')).toHaveLength(3)
    expect(screen.getByRole('note')).toHaveTextContent(
      'Retrieval filters on purchase date. A product bought on 01 Jan 2025 is assessed under Policy v1 even if reviewed today.',
    )
  })

  it('shows the details of the version the user selects', async () => {
    const user = userEvent.setup()
    respondWith([v1, v2, v3])
    renderPage()

    await user.click(await screen.findByRole('button', { name: /Policy v3/ }))

    expect(versionButton(/Policy v3/)).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('heading', { name: 'Aurora Limited Warranty · v3' })).toBeInTheDocument()
    expect(screen.getByText('01 Jan 2027')).toBeInTheDocument()
    expect(screen.getByText('Open-ended')).toBeInTheDocument()
    expect(screen.getByText('EU')).toBeInTheDocument()
  })

  it('shows an empty state when the tenant has no policies', async () => {
    respondWith([])
    renderPage()

    expect(await screen.findByText('No warranty policies are configured for this tenant.')).toBeInTheDocument()
  })

  it('renders the problem details and correlation ID when the request fails', async () => {
    respondWith({ title: 'Service unavailable', status: 503, correlationId: 'c0ffee' }, 503)
    renderPage()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Service unavailable')
    expect(alert).toHaveTextContent('c0ffee')
  })
})
