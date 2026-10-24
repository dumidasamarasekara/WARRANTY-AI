import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { RouterProvider, createMemoryRouter } from 'react-router'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { ClaimDetailPage } from '../../../src/features/claims/ClaimDetailPage'
import { progressSteps, type ClaimDetail } from '../../../src/features/claims/claimDetail'
import type { RemedyView } from '../../../src/features/remedy/remedyQueries'
import { pendingReasonText, remedyOrigin } from '../../../src/features/remedy/remedyView'
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

const claimId = '22222222-2222-2222-2222-222222222222'

/** R2-like: the policy row allows Refund or Replacement, so a reviewer chooses. */
const awaitingChoice: RemedyView = {
  state: 'AwaitingDecision',
  scenario: 'CoveredDefect',
  allowedRemedies: ['Refund', 'Replacement'],
  pendingReason: 'POLICY_CHOICE',
  policyVersion: { policyCode: 'NOR-WP', version: 3 },
  tableRow: 'coveredDefect',
  history: [
    {
      seq: 1,
      kind: 'Evaluated',
      scenario: 'CoveredDefect',
      allowedRemedies: ['Refund', 'Replacement'],
      actor: 'system',
      createdAt: '2026-10-14T08:00:00Z',
    },
  ],
  etag: '"r-5"',
}

/** R1-like: the policy row gives exactly one remedy, set automatically. */
const setByPolicy: RemedyView = {
  state: 'InProgress',
  remedy: 'Repair',
  scenario: 'CoveredDefect',
  allowedRemedies: ['Repair'],
  recommendedRemedy: 'Repair',
  policyVersion: { policyCode: 'AUR-WP', version: 2 },
  tableRow: 'coveredDefect',
  history: [
    {
      seq: 1,
      kind: 'Evaluated',
      scenario: 'CoveredDefect',
      allowedRemedies: ['Repair'],
      remedy: 'Repair',
      decidedBy: 'Policy',
      actor: 'system',
      createdAt: '2026-10-14T08:00:00Z',
    },
  ],
  etag: '"r-2"',
}

const decidedByReviewer: RemedyView = {
  ...awaitingChoice,
  state: 'InProgress',
  remedy: 'Refund',
  pendingReason: undefined,
  history: [
    ...awaitingChoice.history,
    {
      seq: 2,
      kind: 'Decided',
      scenario: 'CoveredDefect',
      allowedRemedies: ['Refund', 'Replacement'],
      remedy: 'Refund',
      decidedBy: 'Reviewer',
      actor: 'Riley Reviewer',
      createdAt: '2026-10-14T10:42:00Z',
    },
  ],
  etag: '"r-6"',
}

/** After a failed repair the reviewer chooses again; a reason is required this time. */
const repairFailedLimit: RemedyView = {
  ...setByPolicy,
  state: 'AwaitingDecision',
  remedy: 'Repair',
  scenario: 'RepairFailed',
  allowedRemedies: ['Replacement', 'Refund'],
  recommendedRemedy: undefined,
  pendingReason: 'REPAIR_FAILED_LIMIT',
  tableRow: 'repairFailed',
  history: [
    ...setByPolicy.history,
    {
      seq: 2,
      kind: 'Evaluated',
      scenario: 'RepairFailed',
      allowedRemedies: ['Replacement', 'Refund'],
      actor: 'system',
      createdAt: '2026-10-20T09:00:00Z',
    },
  ],
  etag: '"r-9"',
}

const approvedClaim = (remedy: RemedyView | undefined): ClaimDetail => ({
  claimId,
  reference: 'WC-2026-000202',
  status: 'Approved',
  finalOutcome: 'Approved',
  finalDecidedBy: 'System',
  channel: 'ClaimantPortal',
  claimDate: '2026-10-10',
  region: 'EU',
  customer: { fullName: '[CUSTOMER]', email: 'customer@example.test', country: 'NO' },
  product: { modelCode: 'NOR-PH-5', serialNumber: 'NP5-0001', name: 'Nordic Phone 5', category: 'Phone', inCatalog: true, claimValue: 480 },
  purchase: { date: '2026-05-01', place: 'Nordic Store', price: 480, currency: 'EUR' },
  problemDescription: 'The phone no longer turns on.',
  evidence: [],
  latestEvaluation: {
    runId: 'cccccccc-0000-0000-0000-000000000001',
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

interface ReceivedDecision {
  ifMatch: string | null
  authorization: string | null
  body: unknown
}

let detail: ClaimDetail = approvedClaim(awaitingChoice)
let detailRequests = 0
let decisions: ReceivedDecision[] = []

const server = setupServer(
  http.get('*/api/me', () =>
    HttpResponse.json({ sub: 'u-1', name: 'Riley Reviewer', tenantDisplayName: 'Nordic Mobile', tenantCurrency: 'EUR', roles: auth.roles }),
  ),
  http.get('*/api/claims/:claimId', () => {
    detailRequests++
    return HttpResponse.json(detail as object)
  }),
)

function onDecision(response: () => Response) {
  server.use(
    http.post('*/api/claims/:claimId/remedy-decisions', async ({ request }) => {
      decisions.push({
        ifMatch: request.headers.get('If-Match'),
        authorization: request.headers.get('Authorization'),
        body: await request.json(),
      })
      return response()
    }),
  )
}

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
beforeEach(() => setStaffAccessTokenProvider(() => 'staff-token'))
afterEach(() => {
  server.resetHandlers()
  setStaffAccessTokenProvider(() => undefined)
  detail = approvedClaim(awaitingChoice)
  detailRequests = 0
  decisions = []
})
afterAll(() => server.close())

function renderTab(roles: string[], path = `/staff/claims/${claimId}/remedy`) {
  auth.roles = roles
  const router = createMemoryRouter(
    [
      { path: '/staff/claims/:claimId/remedy', element: <ClaimDetailPage tab="remedy" /> },
      { path: '/staff/claims/:claimId/*', element: <ClaimDetailPage /> },
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

const remedyCard = async () => within((await screen.findByRole('heading', { name: 'Remedy', level: 2 })).closest('div')!.parentElement!)

async function openDialog(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: 'Decide remedy' }))
  return within(screen.getByRole('dialog', { name: 'Decide remedy' }))
}

describe('remedy presentation', () => {
  it('words the pending reason and the origin per the UI contract', () => {
    expect(pendingReasonText(awaitingChoice)).toBe('The policy allows Refund or Replacement; a reviewer chooses.')
    expect(pendingReasonText(repairFailedLimit)).toBe(
      'The repair failed as many times as the policy allows. The policy allows Replacement or Refund; a reviewer chooses.',
    )
    expect(remedyOrigin(setByPolicy)).toEqual({ actor: 'system', text: 'set by policy AUR-WP v2 · coveredDefect' })
    expect(remedyOrigin(decidedByReviewer)).toMatchObject({ actor: 'human', text: expect.stringMatching(/^decided by Riley Reviewer · /) })
    expect(remedyOrigin(awaitingChoice)).toBeUndefined()
  })

  it('adds a final Remedy step: system when set by policy, human when a reviewer decides, done when completed', () => {
    expect(progressSteps(approvedClaim(undefined)).map((step) => step.key)).not.toContain('remedy')

    const policyStep = progressSteps(approvedClaim(setByPolicy)).at(-1)
    expect(policyStep).toMatchObject({ key: 'remedy', label: 'Remedy', actor: 'system', state: 'current' })
    expect(progressSteps(approvedClaim(awaitingChoice)).at(-1)).toMatchObject({ key: 'remedy', actor: 'human', state: 'current' })
    expect(progressSteps(approvedClaim({ ...decidedByReviewer, state: 'Completed' })).at(-1)).toMatchObject({
      key: 'remedy',
      actor: 'human',
      state: 'done',
    })
  })
})

describe('RemedyTab', () => {
  it('shows a policy-set remedy with its grey origin line, policy basis and history', async () => {
    detail = approvedClaim(setByPolicy)
    renderTab(['claims-agent'])

    const card = await remedyCard()
    expect(card.getByText('Repair · set by policy AUR-WP v2 · coveredDefect')).toBeInTheDocument()
    expect(card.getByText('In progress')).toBeInTheDocument()
    const basis = within(card.getByRole('region', { name: 'Policy basis' }))
    expect(basis.getByText('AUR-WP v2')).toBeInTheDocument()
    expect(basis.getByText('coveredDefect')).toBeInTheDocument()
    expect(within(card.getByRole('list', { name: 'Remedy history' })).getByText('Repair set by policy')).toBeInTheDocument()
    expect(screen.queryByText('AI')).not.toBeInTheDocument()

    const tabs = within(screen.getByRole('navigation', { name: 'Claim sections' }))
    expect(tabs.getByRole('link', { name: 'Remedy' })).toHaveAttribute('aria-current', 'page')
    expect(within(screen.getByRole('list', { name: 'Claim progress' })).getByText(/^Remedy/).closest('li')).toHaveAttribute('aria-current', 'step')
  })

  it('shows the reviewer who decided, with name and time', async () => {
    detail = approvedClaim(decidedByReviewer)
    renderTab(['auditor'])

    const card = await remedyCard()
    expect(card.getByText(/^Refund · decided by Riley Reviewer · /)).toBeInTheDocument()
    expect(within(card.getByRole('list', { name: 'Remedy history' })).getByText('Refund decided')).toBeInTheDocument()
  })

  it('shows the warn banner with the pending reason and no Decide remedy button to auditors and agents', async () => {
    renderTab(['auditor'])

    const card = await remedyCard()
    expect(card.getByText('The policy allows Refund or Replacement; a reviewer chooses.')).toBeInTheDocument()
    expect(card.getByText('Awaiting remedy decision', { selector: 'p' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Decide remedy' })).not.toBeInTheDocument()
  })

  it('opens the dialog from the decide parameter, e.g. a review-queue remedy item', async () => {
    renderTab(['claims-reviewer'], `/staff/claims/${claimId}/remedy?decide`)

    expect(await screen.findByRole('dialog', { name: 'Decide remedy' })).toBeInTheDocument()
  })

  it('offers no Remedy tab for a claim that is not approved', async () => {
    detail = { ...approvedClaim(undefined), status: 'UnderReview', finalOutcome: undefined, finalDecidedBy: undefined }
    renderTab(['claims-reviewer'])

    const tabs = within(await screen.findByRole('navigation', { name: 'Claim sections' }))
    expect(tabs.queryByRole('link', { name: 'Remedy' })).not.toBeInTheDocument()
    expect(tabs.getByRole('link', { name: 'Case file' })).toHaveAttribute('aria-current', 'page')
  })
})

describe('Decide remedy dialog', () => {
  it('lists only the allowed remedies and sends If-Match with the remedy ETag', async () => {
    onDecision(() => HttpResponse.json(decidedByReviewer as object, { status: 201 }))
    const user = renderTab(['claims-reviewer'])

    const dialog = await openDialog(user)
    const radios = dialog.getAllByRole('radio')
    expect(radios.map((radio) => (radio as HTMLInputElement).value)).toEqual(['Refund', 'Replacement'])
    expect(dialog.queryByRole('radio', { name: 'Repair' })).not.toBeInTheDocument()
    expect(radios.every((radio) => !(radio as HTMLInputElement).checked)).toBe(true)
    expect(dialog.getByText('Only remedies the policy allows are listed.')).toBeInTheDocument()

    const submit = dialog.getByRole('button', { name: 'Decide remedy' })
    expect(submit).toBeDisabled()
    await user.click(dialog.getByRole('radio', { name: 'Refund' }))
    expect(submit).toBeEnabled()

    detail = approvedClaim(decidedByReviewer)
    await user.click(submit)

    expect(await screen.findByText('Remedy decided')).toBeInTheDocument()
    expect(decisions).toEqual([{ ifMatch: '"r-5"', authorization: 'Bearer staff-token', body: { remedy: 'Refund' } }])
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await vi.waitFor(() => expect(detailRequests).toBe(2))
    expect(await screen.findByText(/^Refund · decided by Riley Reviewer/)).toBeInTheDocument()
  })

  it('pre-selects the recommendation and requires a reason after the first decision', async () => {
    detail = approvedClaim({ ...repairFailedLimit, recommendedRemedy: 'Replacement' })
    onDecision(() => HttpResponse.json(decidedByReviewer as object, { status: 201 }))
    const user = renderTab(['claims-reviewer'])

    const dialog = await openDialog(user)
    expect(dialog.getByRole('radio', { name: 'Replacement' })).toBeChecked()
    const submit = dialog.getByRole('button', { name: 'Decide remedy' })
    expect(submit).toBeDisabled()

    await user.type(dialog.getByRole('textbox', { name: /Reason/ }), 'Too short')
    expect(submit).toBeDisabled()
    await user.type(dialog.getByRole('textbox', { name: /Reason/ }), ' — repair failed twice.')
    await user.click(submit)

    await vi.waitFor(() =>
      expect(decisions).toEqual([
        { ifMatch: '"r-9"', authorization: 'Bearer staff-token', body: { remedy: 'Replacement', reason: 'Too short — repair failed twice.' } },
      ]),
    )
  })

  it('reloads after 412 and resubmits with the new ETag', async () => {
    let attempt = 0
    onDecision(() =>
      ++attempt === 1
        ? HttpResponse.json({ title: 'Precondition failed', status: 412 }, { status: 412 })
        : HttpResponse.json(decidedByReviewer as object, { status: 201 }),
    )
    const user = renderTab(['claims-reviewer'])

    const dialog = await openDialog(user)
    await user.click(dialog.getByRole('radio', { name: 'Replacement' }))
    await user.click(dialog.getByRole('button', { name: 'Decide remedy' }))

    const alert = await dialog.findByRole('alert')
    expect(alert).toHaveTextContent('This remedy changed since you opened it. Reload to see the latest status.')

    detail = approvedClaim({ ...awaitingChoice, etag: '"r-7"' })
    await user.click(within(alert).getByRole('button', { name: 'Reload' }))
    await vi.waitFor(() => expect(dialog.queryByRole('alert')).not.toBeInTheDocument())
    expect(dialog.getByRole('radio', { name: 'Replacement' })).toBeChecked()

    await user.click(dialog.getByRole('button', { name: 'Decide remedy' }))
    await vi.waitFor(() => expect(decisions.map((decision) => decision.ifMatch)).toEqual(['"r-5"', '"r-7"']))
  })

  it('shows the API message on 409, the self-review refusal on 403 and field errors on 400', async () => {
    const responses = [
      () => HttpResponse.json({ title: 'Conflict', status: 409, detail: 'The product was already handed over; the remedy can no longer change.' }, { status: 409 }),
      () => HttpResponse.json({ title: 'Forbidden', status: 403 }, { status: 403 }),
      () =>
        HttpResponse.json(
          { title: 'Validation failed', status: 400, errors: { remedy: ['Remedy must be one of Refund, Replacement.'] } },
          { status: 400 },
        ),
    ]
    let call = 0
    onDecision(() => responses[call++]!())
    const user = renderTab(['claims-reviewer'])

    const dialog = await openDialog(user)
    await user.click(dialog.getByRole('radio', { name: 'Refund' }))
    const submit = dialog.getByRole('button', { name: 'Decide remedy' })

    await user.click(submit)
    expect(await dialog.findByRole('alert')).toHaveTextContent('The product was already handed over; the remedy can no longer change.')
    expect(within(dialog.getByRole('alert')).getByRole('button', { name: 'Reload' })).toBeInTheDocument()

    await user.click(submit)
    await vi.waitFor(() =>
      expect(dialog.getByRole('alert')).toHaveTextContent('You submitted this claim, so another reviewer must decide its remedy.'),
    )

    await user.click(submit)
    expect(await dialog.findByText('Remedy must be one of Refund, Replacement.')).toBeInTheDocument()
    expect(dialog.queryByRole('alert')).not.toBeInTheDocument()
  })
})
