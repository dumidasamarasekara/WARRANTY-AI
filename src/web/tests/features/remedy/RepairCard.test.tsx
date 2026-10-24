import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { RouterProvider, createMemoryRouter } from 'react-router'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { ClaimDetailPage } from '../../../src/features/claims/ClaimDetailPage'
import type { ClaimDetail } from '../../../src/features/claims/claimDetail'
import type { RemedyView, RepairCentre, RepairView } from '../../../src/features/remedy/remedyQueries'
import { repairActions, repairEventOrigin } from '../../../src/features/remedy/remedyView'
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

const claimId = '33333333-3333-3333-3333-333333333333'

const remedy: RemedyView = {
  state: 'InProgress',
  remedy: 'Repair',
  scenario: 'CoveredDefect',
  allowedRemedies: ['Repair'],
  recommendedRemedy: 'Repair',
  policyVersion: { policyCode: 'AUR-WP', version: 2 },
  tableRow: 'coveredDefect',
  history: [],
  etag: '"r-2"',
}

const northCentre: RepairCentre = {
  id: 'aaaaaaaa-0000-0000-0000-000000000001',
  name: 'North Repair Centre',
  region: 'EU',
  address: '1 Repair Street',
  openingHours: 'Mon–Fri 09:00–17:00',
  contact: '[CENTRE CONTACT]',
  suggested: true,
  servesClaim: true,
}

const southCentre: RepairCentre = {
  ...northCentre,
  id: 'aaaaaaaa-0000-0000-0000-000000000002',
  name: 'South Repair Centre',
  address: '2 Repair Street',
  suggested: false,
}

const awaitingHandover: RepairView = {
  status: 'AwaitingHandover',
  activeCentre: northCentre,
  failedAttempts: 0,
  attemptLimit: 2,
  allowedEvents: ['CentreChanged', 'HandedOver'],
  events: [],
  etag: '"p-1"',
}

const atCentre: RepairView = {
  ...awaitingHandover,
  status: 'AtRepairCentre',
  handedOverOn: '2026-10-15',
  centreReference: 'NRC-4411',
  allowedEvents: ['EstimateReported', 'MarkedReady', 'RepairFailed', 'Transferred', 'Correction'],
  events: [
    {
      seq: 1,
      type: 'HandedOver',
      fromStatus: 'AwaitingHandover',
      toStatus: 'AtRepairCentre',
      reportedAt: '2026-10-15T09:10:00Z',
      recordedAt: '2026-10-15T09:14:00Z',
      source: 'Phone',
      actor: '[AGENT]',
      note: 'Customer dropped it off in the morning.',
    },
  ],
  etag: '"p-2"',
}

const approvedClaim = (repair: RepairView | undefined): ClaimDetail => ({
  claimId,
  reference: 'WC-2026-000303',
  status: 'Approved',
  finalOutcome: 'Approved',
  finalDecidedBy: 'System',
  channel: 'ClaimantPortal',
  claimDate: '2026-10-10',
  region: 'EU',
  customer: { fullName: '[CUSTOMER]', email: 'customer@example.test', country: 'NO' },
  product: { modelCode: 'AUR-HP-2', serialNumber: 'AH2-0001', name: 'Aurora Headphones 2', category: 'Audio', inCatalog: true, claimValue: 320 },
  purchase: { date: '2026-05-01', place: 'Aurora Store', price: 320, currency: 'EUR' },
  problemDescription: 'The left speaker is silent.',
  evidence: [],
  latestEvaluation: {
    runId: 'cccccccc-0000-0000-0000-000000000003',
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
  repair,
})

interface ReceivedEvent {
  ifMatch: string | null
  body: Record<string, unknown>
}

let detail: ClaimDetail = approvedClaim(awaitingHandover)
let detailRequests = 0
let events: ReceivedEvent[] = []

const server = setupServer(
  http.get('*/api/me', () =>
    HttpResponse.json({ sub: 'u-1', name: '[AGENT]', tenantDisplayName: 'Aurora Audio', tenantCurrency: 'EUR', roles: auth.roles }),
  ),
  http.get('*/api/claims/:claimId/repair-centres', () => HttpResponse.json([northCentre, southCentre])),
  http.get('*/api/claims/:claimId', () => {
    detailRequests++
    return HttpResponse.json(detail as object)
  }),
)

function onEvent(response: () => Response) {
  server.use(
    http.post('*/api/claims/:claimId/repair/events', async ({ request }) => {
      events.push({ ifMatch: request.headers.get('If-Match'), body: (await request.json()) as Record<string, unknown> })
      return response()
    }),
  )
}

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
beforeEach(() => setStaffAccessTokenProvider(() => 'staff-token'))
afterEach(() => {
  server.resetHandlers()
  setStaffAccessTokenProvider(() => undefined)
  detail = approvedClaim(awaitingHandover)
  detailRequests = 0
  events = []
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

const repairCard = async () => within((await screen.findByRole('heading', { name: 'Repair', level: 2 })).closest('div')!.parentElement!)

const actionNames = () =>
  within(screen.getByRole('group', { name: 'Repair actions' }))
    .getAllByRole('button')
    .map((button) => button.textContent)

async function openDialog(user: ReturnType<typeof userEvent.setup>, action: string) {
  await user.click(await screen.findByRole('button', { name: action }))
  return within(screen.getByRole('dialog', { name: action }))
}

describe('repair actions', () => {
  it('renders buttons only from allowedEvents, with one primary for the next happy-path step', () => {
    expect(repairActions(awaitingHandover)).toEqual([
      { type: 'HandedOver', primary: true },
      { type: 'CentreChanged', primary: false },
    ])
    expect(repairActions(atCentre)).toEqual([{ type: 'MarkedReady', primary: true }])
    expect(repairActions({ allowedEvents: ['Collected'] })).toEqual([{ type: 'Collected', primary: true }])
    expect(repairActions({ allowedEvents: [] })).toEqual([])
  })

  it('words the event origin with source and both times', () => {
    expect(repairEventOrigin(atCentre.events[0]!)).toMatch(/^reported by phone .+ · recorded by \[AGENT\] .+$/)
  })
})

describe('RepairCard', () => {
  it('shows the status, the active centre with its staff contact and the allowed actions', async () => {
    renderTab(['claims-agent'])

    const card = await repairCard()
    expect(card.getByText('Awaiting hand-over')).toBeInTheDocument()
    const centre = within(card.getByRole('region', { name: 'Repair centre' }))
    expect(centre.getByText('North Repair Centre')).toBeInTheDocument()
    expect(centre.getByText('1 Repair Street')).toBeInTheDocument()
    expect(centre.getByText('[CENTRE CONTACT]')).toBeInTheDocument()
    expect(actionNames()).toEqual(['Record hand-over', 'Change centre'])
    expect(screen.getByRole('button', { name: 'Record hand-over' }).className).toMatch(/primary/)
    expect(screen.queryByRole('button', { name: 'Mark ready' })).not.toBeInTheDocument()
    expect(screen.queryByText('AI')).not.toBeInTheDocument()
  })

  it('follows allowedEvents at the repair centre and shows the events timeline', async () => {
    detail = approvedClaim(atCentre)
    renderTab(['claims-reviewer'])

    const card = await repairCard()
    expect(card.getByText('At repair centre')).toBeInTheDocument()
    expect(card.getByText('NRC-4411')).toBeInTheDocument()
    expect(actionNames()).toEqual(['Mark ready'])
    expect(screen.queryByRole('button', { name: 'Record hand-over' })).not.toBeInTheDocument()

    const timeline = within(screen.getByRole('list', { name: 'Repair events' }))
    expect(timeline.getByText('Handed over')).toBeInTheDocument()
    expect(timeline.getByText(/^reported by phone .* · recorded by \[AGENT\]/)).toBeInTheDocument()
    expect(timeline.getByText('Awaiting hand-over → At repair centre')).toBeInTheDocument()
    expect(timeline.getByText('Customer dropped it off in the morning.')).toBeInTheDocument()
  })

  it('shows auditors every repair detail read-only', async () => {
    detail = approvedClaim(atCentre)
    renderTab(['auditor'])

    const card = await repairCard()
    expect(card.getByText('North Repair Centre')).toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Repair actions' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Mark ready' })).not.toBeInTheDocument()
    expect(screen.getByRole('list', { name: 'Repair events' })).toBeInTheDocument()
  })

  it('shows no repair card when the remedy is not Repair', async () => {
    detail = { ...approvedClaim(undefined), remedy: { ...remedy, remedy: 'Refund', allowedRemedies: ['Refund'], recommendedRemedy: 'Refund' } }
    renderTab(['claims-agent'])

    expect(await screen.findByRole('heading', { name: 'Remedy', level: 2 })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Repair', level: 2 })).not.toBeInTheDocument()
  })
})

describe('Repair event dialogs', () => {
  it('records a hand-over with If-Match and the hand-over fields', async () => {
    onEvent(() => HttpResponse.json(atCentre as object, { status: 201 }))
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Record hand-over')
    const submit = dialog.getByRole('button', { name: 'Record hand-over' })
    expect(submit).toBeDisabled()

    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'Phone')
    const handedOver = dialog.getByLabelText(/Handed over on/)
    await user.clear(handedOver)
    await user.type(handedOver, '2026-10-15')
    await user.type(dialog.getByRole('textbox', { name: /Centre reference/ }), 'NRC-4411')
    await user.type(dialog.getByRole('textbox', { name: /Note/ }), 'Dropped off by the customer.')

    detail = approvedClaim(atCentre)
    await user.click(submit)

    expect(await screen.findByText('Repair update recorded')).toBeInTheDocument()
    expect(events).toHaveLength(1)
    expect(events[0]!.ifMatch).toBe('"p-1"')
    expect(events[0]!.body).toEqual({
      type: 'HandedOver',
      reportedAt: expect.stringMatching(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:00\.000Z$/),
      source: 'Phone',
      note: 'Dropped off by the customer.',
      handedOverOn: '2026-10-15',
      centreReference: 'NRC-4411',
    })
    expect(new Date(events[0]!.body.reportedAt as string).getTime()).toBeLessThanOrEqual(Date.now())
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await vi.waitFor(() => expect(detailRequests).toBe(2))
    expect(await screen.findByText('At repair centre')).toBeInTheDocument()
  })

  it('changes the centre with a reason, listing only the other centres', async () => {
    onEvent(() => HttpResponse.json({ ...awaitingHandover, activeCentre: southCentre } as object, { status: 201 }))
    const user = renderTab(['claims-reviewer'])

    const dialog = await openDialog(user, 'Change centre')
    const centreSelect = dialog.getByRole('combobox', { name: /New repair centre/ })
    await vi.waitFor(() => expect(within(centreSelect).getByRole('option', { name: /South Repair Centre/ })).toBeInTheDocument())
    expect(within(centreSelect).queryByRole('option', { name: /North Repair Centre/ })).not.toBeInTheDocument()

    await user.selectOptions(centreSelect, southCentre.id)
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'Email')
    const submit = dialog.getByRole('button', { name: 'Change centre' })
    await user.type(dialog.getByRole('textbox', { name: /Reason/ }), 'Short')
    expect(submit).toBeDisabled()
    await user.type(dialog.getByRole('textbox', { name: /Reason/ }), ' — the north centre is closed.')
    await user.click(submit)

    await vi.waitFor(() => expect(events).toHaveLength(1))
    expect(events[0]!.ifMatch).toBe('"p-1"')
    expect(events[0]!.body).toMatchObject({
      type: 'CentreChanged',
      source: 'Email',
      repairCentreId: southCentre.id,
      reason: 'Short — the north centre is closed.',
    })
    expect(events[0]!.body).not.toHaveProperty('handedOverOn')
  })

  it('marks ready with only the reported time and source', async () => {
    detail = approvedClaim(atCentre)
    onEvent(() => HttpResponse.json({ ...atCentre, status: 'ReadyForCollection', allowedEvents: ['Collected'] } as object, { status: 201 }))
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Mark ready')
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'InPerson')
    await user.click(dialog.getByRole('button', { name: 'Mark ready' }))

    await vi.waitFor(() => expect(events).toHaveLength(1))
    expect(events[0]!.ifMatch).toBe('"p-2"')
    expect(Object.keys(events[0]!.body).sort()).toEqual(['reportedAt', 'source', 'type'])
    expect(events[0]!.body).toMatchObject({ type: 'MarkedReady', source: 'InPerson' })
  })

  it('records a collection', async () => {
    detail = approvedClaim({ ...atCentre, status: 'ReadyForCollection', allowedEvents: ['Collected', 'Correction'], etag: '"p-3"' })
    onEvent(() => HttpResponse.json({ ...atCentre, status: 'Collected', allowedEvents: [] } as object, { status: 201 }))
    const user = renderTab(['claims-agent'])

    expect(await screen.findByRole('button', { name: 'Record collection' })).toBeInTheDocument()
    const dialog = await openDialog(user, 'Record collection')
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'Other')
    await user.click(dialog.getByRole('button', { name: 'Record collection' }))

    await vi.waitFor(() => expect(events).toEqual([{ ifMatch: '"p-3"', body: expect.objectContaining({ type: 'Collected', source: 'Other' }) }]))
  })

  it('rejects a reported time in the future', async () => {
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Record hand-over')
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'Phone')
    const reported = dialog.getByLabelText(/Reported/)
    await user.clear(reported)
    await user.type(reported, '2099-01-01T10:00')

    expect(dialog.getByText('The reported time cannot be in the future.')).toBeInTheDocument()
    expect(dialog.getByRole('button', { name: 'Record hand-over' })).toBeDisabled()
  })

  it('shows the API message on 409 with Reload', async () => {
    const message = 'This repair is At repair centre; allowed: Mark ready, Repair failed, Record estimate.'
    onEvent(() => HttpResponse.json({ title: 'Conflict', status: 409, detail: message }, { status: 409 }))
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Record hand-over')
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'Phone')
    await user.click(dialog.getByRole('button', { name: 'Record hand-over' }))

    const alert = await dialog.findByRole('alert')
    expect(alert).toHaveTextContent(message)
    expect(within(alert).getByRole('button', { name: 'Reload' })).toBeInTheDocument()
  })

  it('shows the 412 message, reloads and resubmits with the new ETag', async () => {
    let attempt = 0
    onEvent(() =>
      ++attempt === 1
        ? HttpResponse.json({ title: 'Precondition failed', status: 412 }, { status: 412 })
        : HttpResponse.json(atCentre as object, { status: 201 }),
    )
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Record hand-over')
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'Phone')
    await user.click(dialog.getByRole('button', { name: 'Record hand-over' }))

    const alert = await dialog.findByRole('alert')
    expect(alert).toHaveTextContent('This repair changed since you opened it. Reload to see the latest status.')

    detail = approvedClaim({ ...awaitingHandover, etag: '"p-9"' })
    await user.click(within(alert).getByRole('button', { name: 'Reload' }))
    await vi.waitFor(() => expect(dialog.queryByRole('alert')).not.toBeInTheDocument())
    expect(dialog.getByRole('combobox', { name: /Source/ })).toHaveValue('Phone')

    await user.click(dialog.getByRole('button', { name: 'Record hand-over' }))
    await vi.waitFor(() => expect(events.map((event) => event.ifMatch)).toEqual(['"p-1"', '"p-9"']))
  })
})
