import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { RouterProvider, createMemoryRouter } from 'react-router'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { ClaimDetailPage } from '../../../src/features/claims/ClaimDetailPage'
import type { ClaimDetail } from '../../../src/features/claims/claimDetail'
import type { RemedyView } from '../../../src/features/remedy/remedyQueries'
import { setStaffAccessTokenProvider } from '../../../src/shared/api/client'

const auth = vi.hoisted(() => ({ roles: ['claims-agent'] as string[] }))

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    isLoading: false,
    user: {
      access_token: `header.${btoa(JSON.stringify({ realm_access: { roles: auth.roles } }))}.signature`,
      profile: { sub: 'u-1', name: '[AGENT]' },
    },
    signoutRedirect: vi.fn(),
  }),
  hasAuthParams: () => false,
}))

const claimId = '44444444-4444-4444-4444-444444444444'

const replacement: RemedyView = {
  state: 'InProgress',
  remedy: 'Replacement',
  scenario: 'CoveredDefect',
  allowedRemedies: ['Replacement'],
  recommendedRemedy: 'Replacement',
  policyVersion: { policyCode: 'BOR-WP', version: 1 },
  tableRow: 'coveredDefect',
  history: [],
  etag: '"r-3"',
}

const refund: RemedyView = { ...replacement, remedy: 'Refund', allowedRemedies: ['Refund'], recommendedRemedy: 'Refund', etag: '"r-4"' }

const refundCompleted: RemedyView = {
  ...refund,
  state: 'Completed',
  completion: { completedOn: '2026-10-20', reference: 'RF-20261020-7', recordedBy: '[AGENT]' },
  etag: '"r-5"',
}

const approvedClaim = (remedy: RemedyView): ClaimDetail => ({
  claimId,
  reference: 'WC-2026-000404',
  status: 'Approved',
  finalOutcome: 'Approved',
  finalDecidedBy: 'System',
  channel: 'ClaimantPortal',
  claimDate: '2026-10-10',
  region: 'EU',
  customer: { fullName: '[CUSTOMER]', email: 'customer@example.test', country: 'NO' },
  product: { modelCode: 'BOR-TV-1', serialNumber: 'BT1-0001', name: 'Borealis TV', category: 'TV', inCatalog: true, claimValue: 900 },
  purchase: { date: '2026-05-01', place: 'Borealis Store', price: 900, currency: 'EUR' },
  problemDescription: 'The screen stays black.',
  evidence: [],
  latestEvaluation: {
    runId: 'cccccccc-0000-0000-0000-000000000004',
    round: 1,
    status: 'Completed',
    validation: [],
    evidenceFindings: [],
    policyReferences: [],
    guardrails: { disposition: 'AutoApprove', reasons: [], checks: [] },
  },
  reviewDecisions: [],
  requestedItems: [],
  autoInfoRequestCount: 0,
  reviewerInfoRequested: false,
  remedy,
})

interface ReceivedCompletion {
  ifMatch: string | null
  body: unknown
}

let detail: ClaimDetail = approvedClaim(replacement)
let completions: ReceivedCompletion[] = []

const server = setupServer(
  http.get('*/api/me', () =>
    HttpResponse.json({ sub: 'u-1', name: '[AGENT]', tenantDisplayName: 'Borealis', tenantCurrency: 'EUR', roles: auth.roles }),
  ),
  http.get('*/api/claims/:claimId', () => HttpResponse.json(detail as object)),
)

function onCompletion(response: () => Response) {
  server.use(
    http.post('*/api/claims/:claimId/remedy-completions', async ({ request }) => {
      completions.push({ ifMatch: request.headers.get('If-Match'), body: await request.json() })
      return response()
    }),
  )
}

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
beforeEach(() => setStaffAccessTokenProvider(() => 'staff-token'))
afterEach(() => {
  server.resetHandlers()
  setStaffAccessTokenProvider(() => undefined)
  detail = approvedClaim(replacement)
  completions = []
})
afterAll(() => server.close())

function renderTab(roles: string[]) {
  auth.roles = roles
  const router = createMemoryRouter([{ path: '/staff/claims/:claimId/remedy', element: <ClaimDetailPage tab="remedy" /> }], {
    initialEntries: [`/staff/claims/${claimId}/remedy`],
  })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return userEvent.setup()
}

const completionCard = async () =>
  within((await screen.findByRole('heading', { name: 'Completion', level: 2 })).closest('div')!.parentElement!)

const pad = (value: number) => String(value).padStart(2, '0')
const today = () => {
  const now = new Date()
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`
}

const replacementView: NonNullable<ClaimDetail['replacement']> = {
  status: 'ReadyForCollection',
  productRule: 'SameModel',
  returnRule: 'AtCollection',
  returnState: 'NotYetReturned',
  allowedEvents: ['HandedToCustomer'],
  events: [],
  etag: '"x-1"',
}

describe('CompletionCard', () => {
  it.each([['InProgress'], ['Completed']] as const)(
    'defers a %s Replacement with a replacement to the Replacement card: no completion card, no form',
    async (state) => {
      detail = { ...approvedClaim({ ...replacement, state }), replacement: replacementView }
      renderTab(['claims-agent'])

      expect(await screen.findByRole('heading', { name: 'Remedy', level: 2 })).toBeInTheDocument()
      expect(screen.queryByRole('heading', { name: 'Completion' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Record completion' })).not.toBeInTheDocument()
    },
  )

  it('shows an in-progress Replacement decided before 003 (no replacement) read-only: no completion form', async () => {
    renderTab(['claims-agent'])

    const card = await completionCard()
    expect(card.getByText('The replacement has not been handed to the customer yet.')).toBeInTheDocument()
    expect(card.queryByRole('button', { name: 'Record completion' })).not.toBeInTheDocument()
    expect(card.queryByRole('textbox')).not.toBeInTheDocument()
  })

  it('shows a Replacement completed before 003 (no replacement) read-only', async () => {
    detail = approvedClaim({ ...replacement, state: 'Completed', completion: { completedOn: '2026-10-20', recordedBy: '[AGENT]' }, etag: '"r-6"' })
    renderTab(['claims-agent'])

    const card = await completionCard()
    expect(card.getByText('Replacement handed to customer')).toBeInTheDocument()
    expect(card.queryByRole('button')).not.toBeInTheDocument()
  })

  it('records a Refund with today’s date, the reference and If-Match on the remedy ETag', async () => {
    detail = approvedClaim(refund)
    onCompletion(() => HttpResponse.json({ ...refund, state: 'Completed' } as object, { status: 201 }))
    const user = renderTab(['claims-agent'])

    const card = await completionCard()
    const date = card.getByLabelText(/Refund completed on/) as HTMLInputElement
    expect(date.value).toBe(today())
    expect(date.max).toBe(today())
    await user.type(card.getByRole('textbox', { name: /Refund reference/ }), 'RF-20261024-2')

    detail = approvedClaim({ ...refund, state: 'Completed', completion: { completedOn: today(), reference: 'RF-20261024-2', recordedBy: '[AGENT]' }, etag: '"r-6"' })
    await user.click(card.getByRole('button', { name: 'Record completion' }))

    expect(await screen.findByText('Completion recorded')).toBeInTheDocument()
    expect(completions).toEqual([{ ifMatch: '"r-4"', body: { remedy: 'Refund', completedOn: today(), reference: 'RF-20261024-2' } }])
    const updated = await completionCard()
    expect(updated.getByText('Refund completed')).toBeInTheDocument()
    expect(updated.queryByRole('button', { name: 'Record completion' })).not.toBeInTheDocument()
  })

  it('requires the reference for a Refund', async () => {
    detail = approvedClaim(refund)
    onCompletion(() => HttpResponse.json({ ...refund, state: 'Completed' } as object, { status: 201 }))
    const user = renderTab(['claims-reviewer'])

    const card = await completionCard()
    const submit = card.getByRole('button', { name: 'Record completion' })
    const reference = card.getByRole('textbox', { name: /Refund reference/ })
    expect(reference).toBeRequired()
    expect(submit).toBeDisabled()

    await user.type(reference, '  RF-20261024-1 ')
    expect(submit).toBeEnabled()
    await user.click(submit)

    await vi.waitFor(() =>
      expect(completions).toEqual([{ ifMatch: '"r-4"', body: { remedy: 'Refund', completedOn: today(), reference: 'RF-20261024-1' } }]),
    )
  })

  it('refuses a completion date in the future', async () => {
    detail = approvedClaim(refund)
    const user = renderTab(['claims-agent'])

    const card = await completionCard()
    await user.type(card.getByRole('textbox', { name: /Refund reference/ }), 'RF-1')
    fireEvent.change(card.getByLabelText(/Refund completed on/), { target: { value: '2099-01-01' } })

    expect(card.getByText('The date cannot be in the future.')).toBeInTheDocument()
    expect(card.getByRole('button', { name: 'Record completion' })).toBeDisabled()
  })

  it('shows a completed Refund read-only with its date, reference and recorder', async () => {
    detail = approvedClaim(refundCompleted)
    renderTab(['claims-agent'])

    const card = await completionCard()
    expect(card.getByText('Refund completed')).toBeInTheDocument()
    expect(card.getByText('RF-20261020-7')).toBeInTheDocument()
    expect(card.getByText('[AGENT]')).toBeInTheDocument()
    expect(card.queryByRole('button')).not.toBeInTheDocument()
    expect(screen.queryByText('Completion recorded')).not.toBeInTheDocument()
  })

  it('shows auditors the card without the form', async () => {
    detail = approvedClaim(refund)
    renderTab(['auditor'])

    const card = await completionCard()
    expect(card.getByText('The refund has not been completed yet.')).toBeInTheDocument()
    expect(card.queryByRole('button', { name: 'Record completion' })).not.toBeInTheDocument()
    expect(card.queryByRole('textbox')).not.toBeInTheDocument()
  })

  it('shows no completion card for a Repair or a remedy awaiting a decision', async () => {
    detail = approvedClaim({ ...replacement, state: 'AwaitingDecision', remedy: undefined, allowedRemedies: ['Refund', 'Replacement'] })
    renderTab(['claims-agent'])

    expect(await screen.findByRole('heading', { name: 'Remedy', level: 2 })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Completion' })).not.toBeInTheDocument()
  })

  it('shows the API message on 409 and the changed message on 412, reloading for a fresh ETag', async () => {
    let attempt = 0
    onCompletion(() =>
      ++attempt === 1
        ? HttpResponse.json({ title: 'Conflict', status: 409, detail: 'The remedy is already completed.' }, { status: 409 })
        : attempt === 2
          ? HttpResponse.json({ title: 'Precondition failed', status: 412 }, { status: 412 })
          : HttpResponse.json({ ...refund, state: 'Completed' } as object, { status: 201 }),
    )
    detail = approvedClaim(refund)
    const user = renderTab(['claims-agent'])

    const card = await completionCard()
    await user.type(card.getByRole('textbox', { name: /Refund reference/ }), 'RF-1')
    const submit = card.getByRole('button', { name: 'Record completion' })
    await user.click(submit)
    const conflict = await card.findByRole('alert')
    expect(conflict).toHaveTextContent('The remedy is already completed.')
    expect(within(conflict).getByRole('button', { name: 'Reload' })).toBeInTheDocument()

    await user.click(submit)
    await vi.waitFor(() =>
      expect(card.getByRole('alert')).toHaveTextContent('This remedy changed since you opened it. Reload to see the latest status.'),
    )

    detail = approvedClaim({ ...refund, etag: '"r-8"' })
    await user.click(within(card.getByRole('alert')).getByRole('button', { name: 'Reload' }))
    await vi.waitFor(() => expect(card.queryByRole('alert')).not.toBeInTheDocument())

    await user.click(card.getByRole('button', { name: 'Record completion' }))
    await vi.waitFor(() => expect(completions.map((completion) => completion.ifMatch)).toEqual(['"r-4"', '"r-4"', '"r-8"']))
  })

  it('attaches 400 field errors to the fields', async () => {
    onCompletion(() =>
      HttpResponse.json(
        { title: 'Validation failed', status: 400, errors: { completedOn: ['The completion date cannot be in the future.'] } },
        { status: 400 },
      ),
    )
    detail = approvedClaim(refund)
    const user = renderTab(['claims-agent'])

    const card = await completionCard()
    await user.type(card.getByRole('textbox', { name: /Refund reference/ }), 'RF-1')
    await user.click(card.getByRole('button', { name: 'Record completion' }))

    expect(await card.findByText('The completion date cannot be in the future.')).toBeInTheDocument()
    expect(card.queryByRole('alert')).not.toBeInTheDocument()
  })
})
