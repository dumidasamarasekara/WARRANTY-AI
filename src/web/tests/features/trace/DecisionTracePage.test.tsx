import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { RouterProvider, createMemoryRouter } from 'react-router'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { ClaimDetailPage } from '../../../src/features/claims/ClaimDetailPage'
import type { ClaimDetail } from '../../../src/features/claims/claimDetail'
import { DecisionTracePage } from '../../../src/features/trace/DecisionTracePage'
import type { DecisionTrace } from '../../../src/features/trace/decisionTrace'
import { setStaffAccessTokenProvider } from '../../../src/shared/api/client'

const auth = vi.hoisted(() => ({ roles: ['auditor'] as string[] }))

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    isLoading: false,
    user: {
      access_token: `header.${btoa(JSON.stringify({ realm_access: { roles: auth.roles } }))}.signature`,
      profile: { sub: 'u-2', name: 'Avery Auditor' },
    },
    signoutRedirect: vi.fn(),
  }),
  hasAuthParams: () => false,
}))

const claimId = '22222222-2222-2222-2222-222222222222'
const reviewerSub = '9f1c2d3e-0000-0000-0000-000000000007'

// The generated type of a free-form `{ type: object }` is `Record<string, never>`.
const regionFilter = { region: 'NA' } as unknown as Record<string, never>

/** An escalated claim decided by a reviewer, shaped like `DecisionTrace` with T104's AI execution details. */
const verifiedTrace: DecisionTrace = {
  claimId,
  integrity: { hashChainValid: true },
  entries: [
    { seq: 1, occurredAt: '2026-10-02T09:41:07', step: 'ClaimSubmitted', actor: 'system', summary: 'Claim submitted through the claimant portal', correlationId: 'corr-0001' },
    {
      seq: 2,
      occurredAt: '2026-10-02T09:41:12',
      step: 'PolicyRetrieved',
      actor: 'policy',
      summary: 'Retrieved the policy version in force on the purchase date',
      ragQueries: [{ namespaces: ['tenant-aurora/policies'], filters: regionFilter, resultClauseKeys: ['AUR-WP-2.1', 'AUR-WP-4.3'], latencyMs: 84 }],
      toolCalls: [{ tool: 'get_policy_version', allowed: true, latencyMs: 12, summary: 'Aurora Limited Warranty v2' }],
    },
    {
      seq: 3,
      occurredAt: '2026-10-02T09:41:20',
      step: 'AiRecommended',
      actor: 'decision',
      summary: 'AI recommends approval with confidence 92',
      aiCalls: [
        {
          agent: 'decision',
          model: 'claude-decision',
          promptVersion: 'decision@4',
          inputTokens: 4210,
          outputTokens: 388,
          cacheReadTokens: 3000,
          latencyMs: 2140,
          estimatedCost: 0.0187,
          status: 'Ok',
          attempt: 2,
        },
      ],
    },
    { seq: 4, occurredAt: '2026-10-02T09:41:21', step: 'EscalatedToReview', actor: 'system', summary: 'Escalated: claim value above the auto-approval limit' },
    { seq: 5, occurredAt: '2026-10-02T11:05:42', step: 'ReviewerDecided', actor: reviewerSub, summary: 'Reviewer approved the claim' },
  ],
}

let trace: DecisionTrace = verifiedTrace

const detail: ClaimDetail = {
  claimId,
  reference: 'WC-2026-000102',
  status: 'Approved',
  channel: 'ClaimantPortal',
  claimDate: '2026-10-02',
  region: 'NA',
  customer: { fullName: '[CUSTOMER]', email: 'customer@example.test', country: 'US' },
  product: { modelCode: 'AUR-TAB-11', serialNumber: 'AT11-0002', name: 'Aurora Tab 11', category: 'Tablet', inCatalog: true, claimValue: 1400 },
  purchase: { date: '2026-03-01', place: 'Aurora Store', price: 1399.5, currency: 'USD' },
  problemDescription: 'The tablet stopped charging.',
  evidence: [],
  reviewDecisions: [],
  autoInfoRequestCount: 0,
  reviewerInfoRequested: false,
}

const server = setupServer(
  http.get('*/api/me', () => HttpResponse.json({ sub: 'u-2', name: 'Avery Auditor', tenantDisplayName: 'Aurora Electronics', tenantCurrency: 'USD', roles: auth.roles })),
  http.get('*/api/claims/:claimId', () => HttpResponse.json(detail as object)),
  http.get('*/api/claims/:claimId/trace', ({ params }) =>
    params.claimId === claimId
      ? HttpResponse.json(trace as object)
      : HttpResponse.json({ title: 'Not found', status: 404, correlationId: 'c0ffee' }, { status: 404 }),
  ),
)

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
beforeEach(() => setStaffAccessTokenProvider(() => 'staff-token'))
afterEach(() => {
  server.resetHandlers()
  setStaffAccessTokenProvider(() => undefined)
  trace = verifiedTrace
})
afterAll(() => server.close())

function renderTrace(id = claimId, path = '/trace') {
  const router = createMemoryRouter([{ path: '/trace', element: <DecisionTracePage claimId={id} reference="WC-2026-000102" /> }], {
    initialEntries: [path],
  })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return userEvent.setup()
}

const entry = (name: RegExp) => screen.getByRole('article', { name })

describe('DecisionTracePage', () => {
  it('renders the trail as a timeline with step labels, actor tones, summaries and times', async () => {
    renderTrace()

    expect(await screen.findByRole('heading', { name: 'Decision trace' })).toBeInTheDocument()
    expect(screen.getByText(/5 entries · append-only/)).toBeInTheDocument()
    expect(screen.getByText('WC-2026-000102')).toBeInTheDocument()

    const timeline = within(screen.getByRole('list', { name: 'Decision trail' }))
    expect(timeline.getAllByRole('listitem')).toHaveLength(5)
    expect(timeline.getAllByRole('heading', { level: 3 }).map((heading) => heading.textContent)).toEqual([
      '1. Claim submitted',
      '2. Policy retrieved',
      '3. AI recommendation',
      '4. Escalated to human review',
      '5. Reviewer decision',
    ])

    expect(within(entry(/^1\./)).getByText('SYSTEM')).toBeInTheDocument()
    expect(within(entry(/^1\./)).getByText('09:41:07')).toHaveAttribute('title', '02 Oct 2026, 09:41')
    expect(within(entry(/^3\./)).getByText('AI')).toBeInTheDocument()
    expect(within(entry(/^3\./)).getByText('decision')).toBeInTheDocument()
    expect(within(entry(/^3\./)).getByText('AI recommends approval with confidence 92')).toBeInTheDocument()
    expect(within(entry(/^5\./)).getByText('HUMAN')).toBeInTheDocument()
    expect(within(entry(/^5\./)).getByText(reviewerSub)).toBeInTheDocument()

    // Dots: actor tone, except escalations in the human tone.
    const items = timeline.getAllByRole('listitem')
    expect(items[0]).toHaveClass('system')
    expect(items[2]).toHaveClass('ai')
    expect(items[3]).toHaveClass('human')
  })

  it('shows the verified integrity badge', async () => {
    renderTrace()

    expect(await screen.findByText('Hash chain verified')).toHaveClass('ok')
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
  })

  it('flags a broken hash chain with a badge and an alert', async () => {
    trace = { ...verifiedTrace, integrity: { hashChainValid: false } }
    renderTrace()

    expect(await screen.findByText('Integrity check failed', { selector: 'span' })).toHaveClass('err')
    expect(screen.getByRole('status')).toHaveTextContent(/hash chain does not verify/)
  })

  it('never shows an unchecked chain as verified', async () => {
    trace = { ...verifiedTrace, integrity: {} }
    renderTrace()

    expect(await screen.findByText('Integrity not checked')).toBeInTheDocument()
    expect(screen.queryByText('Hash chain verified')).not.toBeInTheDocument()
  })

  it('expands AI call, tool and RAG details in the technical view', async () => {
    const user = renderTrace()

    await screen.findByRole('heading', { name: 'Decision trace' })
    expect(screen.getByRole('button', { name: 'Operational' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.queryByRole('region', { name: 'AI calls' })).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Technical' }))

    expect(screen.getByRole('button', { name: 'Technical' })).toHaveAttribute('aria-pressed', 'true')
    const aiCalls = within(screen.getByRole('region', { name: 'AI calls' }))
    expect(aiCalls.getByText('claude-decision')).toBeInTheDocument()
    expect(aiCalls.getByText('decision@4')).toBeInTheDocument()
    expect(aiCalls.getByText('4,210')).toBeInTheDocument()
    expect(aiCalls.getByText('388')).toBeInTheDocument()
    expect(aiCalls.getByText('3,000')).toBeInTheDocument()
    expect(aiCalls.getByText('2,140 ms')).toBeInTheDocument()
    expect(aiCalls.getByText('USD 0.0187')).toBeInTheDocument()
    expect(aiCalls.getByText('↻ retried')).toBeInTheDocument()

    const tools = within(screen.getByRole('region', { name: 'Tool calls' }))
    expect(tools.getByText('get_policy_version')).toBeInTheDocument()
    expect(tools.getByText('Aurora Limited Warranty v2')).toBeInTheDocument()

    const rag = within(screen.getByRole('region', { name: 'RAG queries' }))
    expect(rag.getByText('tenant-aurora/policies')).toBeInTheDocument()
    expect(rag.getByText('AUR-WP-2.1, AUR-WP-4.3')).toBeInTheDocument()
    expect(rag.getByText('84 ms')).toBeInTheDocument()

    expect(screen.getByText('corr-0001')).toBeInTheDocument()
  })

  it('expands one entry at a time in the operational view', async () => {
    const user = renderTrace()

    const toggle = within(await screen.findByRole('article', { name: /^3\./ })).getByRole('button', { name: 'Show technical details' })
    await user.click(toggle)

    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('region', { name: 'AI calls' })).toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'RAG queries' })).not.toBeInTheDocument()
    // Entries without technical details have nothing to expand.
    expect(within(entry(/^4\./)).queryByRole('button')).not.toBeInTheDocument()
  })

  it('renders the API problem when the trace cannot be loaded', async () => {
    renderTrace('33333333-3333-3333-3333-333333333333')

    expect(await screen.findByRole('alert')).toHaveTextContent('Not found')
    expect(screen.getByText('c0ffee')).toBeInTheDocument()
  })
})

describe('ClaimDetailPage decision trace tab', () => {
  it('embeds the decision trace for an auditor', async () => {
    const router = createMemoryRouter([{ path: '/staff/claims/:claimId/*', element: <ClaimDetailPage /> }], {
      initialEntries: [`/staff/claims/${claimId}/trace?view=technical`],
    })
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>,
    )

    const tabs = within(await screen.findByRole('navigation', { name: 'Claim sections' }))
    expect(tabs.getByRole('link', { name: 'Decision trace' })).toHaveAttribute('aria-current', 'page')
    expect(await screen.findByText('Hash chain verified')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Technical' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('region', { name: 'AI calls' })).toBeInTheDocument()
  })
})
