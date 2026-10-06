import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { RouterProvider, createMemoryRouter } from 'react-router'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { ClaimDetailPage } from '../../../src/features/claims/ClaimDetailPage'
import { progressSteps, type ClaimDetail } from '../../../src/features/claims/claimDetail'
import { setStaffAccessTokenProvider } from '../../../src/shared/api/client'

const auth = vi.hoisted(() => ({ roles: ['claims-reviewer'] as string[] }))

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    isLoading: false,
    user: {
      access_token: `header.${btoa(JSON.stringify({ realm_access: { roles: auth.roles } }))}.signature`,
      profile: { sub: 'u-1', name: 'Riley Reviewer' },
    },
    signoutRedirect: vi.fn(),
  }),
  hasAuthParams: () => false,
}))

const claimId = '11111111-1111-1111-1111-111111111111'
const invoiceId = 'aaaaaaaa-0000-0000-0000-000000000001'
const photoId = 'aaaaaaaa-0000-0000-0000-000000000002'

/** S5-like: escalated because the claim value is above the auto-approval limit (reviewer projection). */
const reviewerView: ClaimDetail = {
  claimId,
  reference: 'WC-2026-000101',
  status: 'UnderReview',
  channel: 'ClaimantPortal',
  claimDate: '2026-10-02',
  region: 'NA',
  customer: { fullName: '[CUSTOMER]', email: 'customer@example.test', country: 'US' },
  product: { modelCode: 'AUR-TAB-11', serialNumber: 'AT11-0001', name: 'Aurora Tab 11', category: 'Tablet', inCatalog: true, claimValue: 1400 },
  purchase: { date: '2026-03-01', place: 'Aurora Store', price: 1399.5, currency: 'USD' },
  problemDescription: 'The tablet stopped charging after two weeks.',
  evidence: [
    { evidenceId: invoiceId, ref: 'EV-1', kind: 'Invoice', fileName: 'invoice.pdf', contentType: 'application/pdf', round: 1 },
    { evidenceId: photoId, ref: 'EV-2', kind: 'Photo', fileName: 'front.jpg', contentType: 'image/jpeg', round: 1 },
  ],
  latestEvaluation: {
    runId: 'bbbbbbbb-0000-0000-0000-000000000001',
    round: 1,
    status: 'Completed',
    validation: [{ code: 'REQUIRED_FIELDS', passed: true }],
    extraction: { problemCategory: 'BATTERY_FAILURE', component: 'CHARGING_PORT', claimedCause: 'SPONTANEOUS_FAILURE', symptoms: ['does not charge'], summary: 'Tablet no longer charges.' },
    evidenceFindings: [
      {
        ref: 'EV-1',
        evidenceId: invoiceId,
        kind: 'InvoiceExtraction',
        result: { sellerName: 'Aurora Store', invoiceNumber: 'INV-77', invoiceDate: '2026-03-01', totalAmount: 1399.5, currency: 'USD', anomalies: [] },
        consistency: [
          { field: 'purchase_date', claimValue: '2026-03-01', evidenceValue: '2026-03-01', match: true },
          { field: 'price', claimValue: '1400.00', evidenceValue: '1399.50', match: false },
        ],
      },
      {
        ref: 'EV-2',
        evidenceId: photoId,
        kind: 'PhotoAnalysis',
        result: { observations: 'Tablet front, no visible damage.', damageTypes: ['NONE_VISIBLE'], consistentWithDescription: 'CONSISTENT', visibleSerial: 'NOT_VISIBLE', imageQuality: 'GOOD' },
        consistency: [],
        confidence: 88,
      },
    ],
    policyReferences: [
      {
        ref: 'POL-1',
        clauseKey: 'AUR-WP-2.1',
        clauseTitle: 'Manufacturing defects',
        clauseType: 'Coverage',
        documentTitle: 'Aurora Limited Warranty',
        version: 2,
        effectiveFrom: '2026-01-01',
        effectiveTo: null,
        excerpt: 'Defects in materials and workmanship are covered for 12 months.',
        cited: true,
      },
    ],
    policyAssessment: { versionOutcome: 'Ok', assessment: { coverageAssessment: 'COVERED' }, confidence: 91, model: 'claude-policy', promptVersion: 'policy@3' },
    risk: { score: 57, level: 'Medium', signals: [{ code: 'HIGH_VALUE_CLAIM', source: 'Deterministic', severity: 'Medium', detail: 'Claim value above typical range' }] },
    recommendation: {
      isValid: true,
      validationErrors: [],
      decision: 'APPROVE',
      coverage: 'COVERED',
      confidence: 92,
      reasoningSummary: 'Battery failure within the warranty period is covered.',
      claimantExplanation: 'Your claim is covered.',
      evidenceRefs: [{ ref: 'EV-2', observation: 'No signs of physical damage' }],
      policyRefs: [{ ref: 'POL-1', relevance: 'SUPPORTS_COVERAGE' }],
      missingInformation: [],
      model: 'claude-decision',
      promptVersion: 'decision@4',
    },
    guardrails: {
      disposition: 'HumanReview',
      reasons: ['VALUE_ABOVE_LIMIT', 'RISK_MEDIUM'],
      checks: [
        { code: 'VALUE_WITHIN_LIMIT', passed: false, expected: '≤ USD 1,000.00', actual: 'USD 1,400.00', message: 'Claim value above the auto-approval limit' },
        { code: 'SCHEMA_VALID', passed: true, message: 'Recommendation matches the schema' },
      ],
    },
  },
  reviewDecisions: [],
  autoInfoRequestCount: 0,
  reviewerInfoRequested: false,
}

/** The same claim as the API projects it for `claims-agent` (FR-005). */
const agentView: ClaimDetail = {
  ...reviewerView,
  latestEvaluation: {
    ...reviewerView.latestEvaluation!,
    risk: undefined,
    recommendation: { ...reviewerView.latestEvaluation!.recommendation!, reasoningSummary: undefined },
    guardrails: { disposition: 'HumanReview', reasons: ['VALUE_ABOVE_LIMIT', 'Additional checks required'] },
  },
}

let detail: ClaimDetail = reviewerView
let contentAuthorization: string | null = null

const server = setupServer(
  http.get('*/api/me', () =>
    HttpResponse.json({ sub: 'u-1', name: 'Riley Reviewer', tenantDisplayName: 'Aurora Electronics', tenantCurrency: 'USD', roles: auth.roles }),
  ),
  http.get('*/api/claims/:claimId', ({ params }) =>
    params.claimId === claimId
      ? HttpResponse.json(detail as object, { headers: { ETag: '"7"' } })
      : HttpResponse.json({ title: 'Not found', status: 404, correlationId: 'c0ffee' }, { status: 404 }),
  ),
  http.get('*/api/claims/:claimId/evidence/:evidenceId/content', ({ request, params }) => {
    contentAuthorization = request.headers.get('Authorization')
    const type = params.evidenceId === invoiceId ? 'application/pdf' : 'image/jpeg'
    return new HttpResponse(new Uint8Array([1, 2, 3]), { headers: { 'Content-Type': type } })
  }),
)

const objectUrls = { createObjectURL: URL.createObjectURL, revokeObjectURL: URL.revokeObjectURL }

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
beforeEach(() => {
  setStaffAccessTokenProvider(() => 'staff-token')
  let next = 0
  URL.createObjectURL = vi.fn(() => `blob:evidence-${++next}`)
  URL.revokeObjectURL = vi.fn()
})
afterEach(() => {
  server.resetHandlers()
  setStaffAccessTokenProvider(() => undefined)
  Object.assign(URL, objectUrls)
  detail = reviewerView
  contentAuthorization = null
})
afterAll(() => server.close())

function renderPage(path: string, roles: string[]) {
  auth.roles = roles
  const router = createMemoryRouter(
    [
      { path: '/staff/claims/:claimId/*', element: <ClaimDetailPage /> },
      { path: '/staff/claims', element: <p>Claims list</p> },
    ],
    { initialEntries: [path] },
  )
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return userEvent.setup()
}

const progress = () => within(screen.getByRole('list', { name: 'Claim progress' }))

describe('progress strip', () => {
  it('marks the evaluated steps done and the review step current for an escalated claim', () => {
    const steps = progressSteps(reviewerView)
    expect(steps.map((step) => `${step.key}:${step.actor}:${step.state}`)).toEqual([
      'submitted:system:done',
      'intake:ai:done',
      'evidence:ai:done',
      'policy:ai:done',
      'risk:system:done',
      'decision:ai:done',
      'guardrails:system:done',
      'outcome:human:done',
      'review:human:current',
    ])
  })

  it('treats risk as done in the agent view when later steps are present, and stops at the running step', () => {
    expect(progressSteps(agentView).find((step) => step.key === 'risk')?.state).toBe('done')

    const running: ClaimDetail = {
      ...reviewerView,
      status: 'UnderEvaluation',
      latestEvaluation: { runId: 'r', round: 1, status: 'Running', validation: [{ code: 'REQUIRED_FIELDS', passed: true }] },
    }
    const steps = progressSteps(running)
    expect(steps.map((step) => step.state)).toEqual(['done', 'done', 'current', 'pending', 'pending', 'pending', 'pending', 'pending'])
  })

  it('completes every step of an auto-approved claim without a review step', () => {
    const approved: ClaimDetail = {
      ...reviewerView,
      status: 'Approved',
      finalOutcome: 'Approved',
      finalDecidedBy: 'System',
      latestEvaluation: { ...reviewerView.latestEvaluation!, guardrails: { disposition: 'AutoApprove', reasons: [], checks: [] } },
    }
    const steps = progressSteps(approved)
    expect(steps.map((step) => step.key)).not.toContain('review')
    expect(steps.every((step) => step.state === 'done')).toBe(true)
  })
})

describe('ClaimDetailPage', () => {
  it('shows the claim header, progress strip and case file for a reviewer', async () => {
    renderPage(`/staff/claims/${claimId}`, ['claims-reviewer'])

    expect(await screen.findByRole('heading', { level: 1, name: 'Aurora Tab 11' })).toBeInTheDocument()
    expect(screen.getByText('WC-2026-000101')).toBeInTheDocument()
    expect(screen.getByText('USD 1,400.00')).toBeInTheDocument()
    expect(progress().getByText(/^Review/).closest('li')).toHaveAttribute('aria-current', 'step')

    const tabs = within(screen.getByRole('navigation', { name: 'Claim sections' }))
    expect(tabs.getByRole('link', { name: 'Case file' })).toHaveAttribute('aria-current', 'page')
    expect(tabs.getByRole('link', { name: 'Decision trace' })).toBeInTheDocument()

    expect(screen.getByText('AT11-0001')).toBeInTheDocument()
    expect(screen.getByText('USD 1,399.50')).toBeInTheDocument()
    expect(screen.getByText('“The tablet stopped charging after two weeks.”')).toBeInTheDocument()
    expect(screen.getByText('Battery failure')).toBeInTheDocument()

    const summary = within(screen.getByRole('region', { name: /Recommendation · not final/ }))
    expect(summary.getByText('Approve')).toBeInTheDocument()
    expect(summary.getByRole('meter', { name: 'Confidence' })).toHaveAttribute('aria-valuenow', '92')
    expect(summary.getByText('Medium · 57')).toBeInTheDocument()
    expect(summary.getByText('AUR-WP-2.1')).toBeInTheDocument()

    const route = within(screen.getByRole('region', { name: 'Outcome route' }))
    expect(route.getByText('Escalated — a reviewer must decide')).toBeInTheDocument()
    expect(route.getByText('Claim value above auto-approval limit')).toBeInTheDocument()
    expect(route.getByText('Medium risk')).toBeInTheDocument()
  })

  it('shows reasoning, guardrail checks, risk signals, citations and agent confidence on the AI decision tab', async () => {
    renderPage(`/staff/claims/${claimId}/decision`, ['claims-reviewer'])

    const panel = within(await screen.findByRole('region', { name: /AI decision/ }))
    expect(panel.getByText('claude-decision · decision@4')).toBeInTheDocument()
    expect(panel.getByText('Risk')).toBeInTheDocument()
    expect(panel.getByText('Battery failure within the warranty period is covered.')).toBeInTheDocument()
    expect(panel.getByText('VALUE_WITHIN_LIMIT')).toBeInTheDocument()
    expect(panel.getByText('Expected ≤ USD 1,000.00 · actual USD 1,400.00')).toBeInTheDocument()
    expect(panel.getByText(/Failed:/)).toBeInTheDocument()
    expect(panel.getByRole('link', { name: 'EV-2' })).toHaveAttribute('href', `/staff/claims/${claimId}/evidence?item=${photoId}`)

    const citation = screen.getByRole('article', { name: 'Aurora Limited Warranty, AUR-WP-2.1' })
    expect(citation).toHaveTextContent('Version 2 · effective 01 Jan 2026 – no end date')
    expect(citation).toHaveTextContent('Defects in materials and workmanship are covered for 12 months.')
    expect(citation).toHaveTextContent('Aurora Electronics only')
    expect(screen.getByRole('meter', { name: 'Policy agent confidence' })).toHaveAttribute('aria-valuenow', '91')

    expect(screen.getByRole('heading', { name: 'Risk signals' })).toBeInTheDocument()
    expect(screen.getByText('HIGH_VALUE_CLAIM')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Open in review' })).toHaveAttribute('href', `/staff/review/${claimId}`)
  })

  it('renders no risk, reasoning or guardrail panels for a claims agent', async () => {
    detail = agentView
    renderPage(`/staff/claims/${claimId}/decision`, ['claims-agent'])

    const panel = within(await screen.findByRole('region', { name: /AI decision/ }))
    expect(panel.getByText('Approve')).toBeInTheDocument()
    expect(panel.queryByText('Risk')).not.toBeInTheDocument()
    expect(screen.queryByText('Why — decision factors')).not.toBeInTheDocument()
    expect(screen.queryByText('VALUE_WITHIN_LIMIT')).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Risk signals' })).not.toBeInTheDocument()
    expect(screen.queryByText(/Medium · 57/)).not.toBeInTheDocument()

    const route = within(screen.getByRole('region', { name: 'Outcome route' }))
    expect(route.getByText('Additional checks required')).toBeInTheDocument()
    expect(screen.getByRole('article', { name: 'Aurora Limited Warranty, AUR-WP-2.1' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Open in review' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Decision trace' })).not.toBeInTheDocument()
  })

  it('gives auditors the decision trace and no review action on an escalated claim', async () => {
    renderPage(`/staff/claims/${claimId}/decision`, ['auditor'])

    expect(await screen.findByRole('region', { name: /AI decision/ })).toBeInTheDocument()
    expect(screen.getByText('Escalated — a reviewer must decide.')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Open in review' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /approve|reject|override|request information/i })).not.toBeInTheDocument()

    const tabs = within(screen.getByRole('navigation', { name: 'Claim sections' }))
    expect(tabs.getByRole('link', { name: 'Decision trace' })).toHaveAttribute('href', `/staff/claims/${claimId}/trace`)
  })

  it('opens the trace tab for auditors', async () => {
    renderPage(`/staff/claims/${claimId}/trace`, ['auditor'])

    const tabs = within(await screen.findByRole('navigation', { name: 'Claim sections' }))
    expect(tabs.getByRole('link', { name: 'Decision trace' })).toHaveAttribute('aria-current', 'page')
  })

  it('shows the authorized original and the Evidence agent findings with its confidence', async () => {
    const user = renderPage(`/staff/claims/${claimId}/evidence?item=${photoId}`, ['claims-agent'])

    expect(await screen.findByRole('img', { name: 'front.jpg (Photo)' })).toHaveAttribute('src', expect.stringMatching(/^blob:evidence-/))
    expect(contentAuthorization).toBe('Bearer staff-token')
    const interpretation = within(screen.getByRole('region', { name: 'AI interpretation' }))
    expect(interpretation.getByText('Tablet front, no visible damage.')).toBeInTheDocument()
    expect(interpretation.getByRole('meter', { name: 'Evidence agent confidence' })).toHaveAttribute('aria-valuenow', '88')

    await user.click(screen.getByRole('button', { name: /invoice\.pdf/ }))

    expect(await screen.findByLabelText('invoice.pdf (Invoice)')).toHaveAttribute('type', 'application/pdf')
    expect(screen.getByText('Verify before relying')).toBeInTheDocument()
    expect(screen.getByText('INV-77')).toBeInTheDocument()
    const price = screen.getByRole('rowheader', { name: 'Price' }).closest('tr')!
    expect(price).toHaveTextContent('1400.001399.50Conflicts')
    expect(screen.getByRole('rowheader', { name: 'Purchase date' }).closest('tr')).toHaveTextContent('Supports')
  })

  it('reports an invalid recommendation and a failed AI step', async () => {
    detail = {
      ...reviewerView,
      latestEvaluation: {
        ...reviewerView.latestEvaluation!,
        recommendation: { ...reviewerView.latestEvaluation!.recommendation!, isValid: false, validationErrors: ['Unknown reference POL-9'] },
        failureReason: 'Provider error',
      },
    }
    renderPage(`/staff/claims/${claimId}/decision`, ['claims-reviewer'])

    expect(await screen.findByText('The AI recommendation failed validation')).toBeInTheDocument()
    expect(screen.getByText('Unknown reference POL-9')).toBeInTheDocument()
    expect(screen.getByText('AI analysis could not be completed — routed to human review')).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: /AI decision/ })).getByText('No AI decision')).toBeInTheDocument()
  })

  it('renders the problem details when the claim is not found', async () => {
    renderPage('/staff/claims/99999999-9999-9999-9999-999999999999', ['claims-agent'])

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Not found')
    expect(alert).toHaveTextContent('c0ffee')
  })
})
