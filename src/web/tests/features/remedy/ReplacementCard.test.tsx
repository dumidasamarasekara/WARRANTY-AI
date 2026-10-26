import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { RouterProvider, createMemoryRouter } from 'react-router'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { ClaimDetailPage } from '../../../src/features/claims/ClaimDetailPage'
import type { ClaimDetail } from '../../../src/features/claims/claimDetail'
import type { RemedyView, RepairCentre, RepairView, ReplacementView } from '../../../src/features/remedy/remedyQueries'
import { needsReturnConfirmation, replacementActions, replacementEventOrigin, replacementPolicyBasis } from '../../../src/features/remedy/replacementView'
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

const remedy: RemedyView = {
  state: 'InProgress',
  remedy: 'Replacement',
  scenario: 'CoveredDefect',
  allowedRemedies: ['Replacement'],
  recommendedRemedy: 'Replacement',
  policyVersion: { policyCode: 'AUR-WP', version: 2 },
  tableRow: 'coveredDefect',
  history: [],
  etag: '"r-2"',
}

const northLocation: RepairCentre = {
  id: 'bbbbbbbb-0000-0000-0000-000000000001',
  name: 'North Service Point',
  region: 'EU',
  address: '1 Service Street',
  openingHours: 'Mon–Fri 09:00–17:00',
  contact: '[LOCATION CONTACT]',
  suggested: true,
  servesClaim: true,
}

const southLocation: RepairCentre = {
  ...northLocation,
  id: 'bbbbbbbb-0000-0000-0000-000000000002',
  name: 'South Service Point',
  address: '2 Service Street',
  suggested: false,
}

const suggested = {
  seq: 1,
  type: 'LocationSuggested',
  toStatus: 'AwaitingUnit' as const,
  reportedAt: '2026-10-20T08:00:00Z',
  recordedAt: '2026-10-20T08:00:00Z',
  source: 'System',
  actor: 'system',
}

const awaitingUnit: ReplacementView = {
  status: 'AwaitingUnit',
  productRule: 'SameCategoryAtLeastClaimValue',
  returnRule: 'AtCollection',
  minimumCoverMonths: 0,
  activeLocation: northLocation,
  returnState: 'NotYetReturned',
  allowedEvents: ['LocationChanged', 'ReturnRecorded', 'ModelProposed', 'UnitReserved', 'Moved', 'NotAvailable'],
  events: [suggested],
  etag: '"x-1"',
}

const ready: ReplacementView = {
  ...awaitingUnit,
  status: 'ReadyForCollection',
  currentUnit: { seq: 1, modelCode: 'AUR-TAB10', serialNumber: 'AT10-9001', result: 'Reserved' },
  allowedEvents: ['ReturnRecorded', 'UnitRefused', 'Moved', 'HandedToCustomer', 'Correction'],
  events: [
    suggested,
    {
      seq: 2,
      type: 'UnitReserved',
      fromStatus: 'AwaitingUnit',
      toStatus: 'ReadyForCollection',
      reportedAt: '2026-10-21T09:10:00Z',
      recordedAt: '2026-10-21T09:14:00Z',
      source: 'Phone',
      actor: '[AGENT]',
      note: 'Unit put aside at the counter.',
    },
  ],
  etag: '"x-2"',
}

/** BOR-style rules: the faulty product comes back before a unit is reserved. */
const awaitingReturn: ReplacementView = {
  ...awaitingUnit,
  status: 'AwaitingReturn',
  productRule: 'SameModel',
  returnRule: 'BeforeReservation',
  allowedEvents: ['LocationChanged', 'ReturnRecorded', 'Moved', 'Correction'],
  etag: '"x-3"',
}

/** A returned product whose serial does not match the claim's, as a reviewer sees it. */
const mismatched: ReplacementView = {
  ...awaitingReturn,
  returnState: 'Mismatch',
  faultyProductReturn: { returnedOn: '2026-10-21', serialSeen: 'AT10-7777', serialMatches: false, heldByRepairCentre: false },
  allowedEvents: ['LocationChanged', 'ReturnAccepted', 'Moved', 'Correction'],
  etag: '"x-4"',
}

/** The product stayed at the repair centre of the 002 repair the replacement follows (FR-020). */
const held: ReplacementView = {
  ...awaitingUnit,
  returnState: 'HeldByRepairCentre',
  faultyProductReturn: { returnedOn: '2026-10-12', serialMatches: true, heldByRepairCentre: true },
  allowedEvents: ['LocationChanged', 'ModelProposed', 'UnitReserved', 'Moved', 'NotAvailable'],
}

const holdingRepair = {
  status: 'NotPossible',
  notPossibleReason: 'Failed',
  activeCentre: { ...northLocation, id: 'bbbbbbbb-0000-0000-0000-000000000009', name: 'Central Repair Workshop' },
  handedOverOn: '2026-10-12',
  failedAttempts: 2,
  attemptLimit: 2,
  allowedEvents: [],
  events: [],
  etag: '"p-1"',
} as unknown as RepairView

const approvedClaim = (replacement: ReplacementView | undefined): ClaimDetail => ({
  claimId,
  reference: 'WC-2026-000404',
  status: 'Approved',
  finalOutcome: 'Approved',
  finalDecidedBy: 'System',
  channel: 'ClaimantPortal',
  claimDate: '2026-10-18',
  region: 'EU',
  customer: { fullName: '[CUSTOMER]', email: 'customer@example.test', country: 'NO' },
  product: { modelCode: 'AUR-TAB10', serialNumber: 'AT10-0001', name: 'Aurora Tab 10', category: 'tablet', inCatalog: true, claimValue: 450 },
  purchase: { date: '2026-05-01', place: 'Aurora Store', price: 450, currency: 'EUR' },
  problemDescription: 'The screen flickers.',
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
  replacement,
})

interface ReceivedEvent {
  ifMatch: string | null
  body: Record<string, unknown>
}

let detail: ClaimDetail = approvedClaim(awaitingUnit)
let detailRequests = 0
let events: ReceivedEvent[] = []

const server = setupServer(
  http.get('*/api/me', () =>
    HttpResponse.json({ sub: 'u-1', name: '[AGENT]', tenantDisplayName: 'Aurora Audio', tenantCurrency: 'EUR', roles: auth.roles }),
  ),
  http.get('*/api/claims/:claimId/replacement-locations', () => HttpResponse.json([northLocation, southLocation])),
  http.get('*/api/claims/:claimId', () => {
    detailRequests++
    return HttpResponse.json(detail as object)
  }),
)

function onEvent(response: () => Response) {
  server.use(
    http.post('*/api/claims/:claimId/replacement/events', async ({ request }) => {
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
  detail = approvedClaim(awaitingUnit)
  detailRequests = 0
  events = []
})
afterAll(() => server.close())

function renderTab(roles: string[], search = '') {
  auth.roles = roles
  const router = createMemoryRouter([{ path: '/staff/claims/:claimId/remedy', element: <ClaimDetailPage tab="remedy" /> }], {
    initialEntries: [`/staff/claims/${claimId}/remedy${search}`],
  })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return userEvent.setup()
}

const replacementCard = async () =>
  within((await screen.findByRole('heading', { name: 'Replacement', level: 2 })).closest('div')!.parentElement!)

const actionNames = () =>
  within(screen.getByRole('group', { name: 'Replacement actions' }))
    .getAllByRole('button')
    .map((button) => button.textContent)

async function openDialog(user: ReturnType<typeof userEvent.setup>, action: string) {
  await user.click(await screen.findByRole('button', { name: action }))
  return within(screen.getByRole('dialog', { name: action }))
}

describe('replacement view', () => {
  it('renders buttons only from allowedEvents that have a dialog, with one primary for the next happy-path step', () => {
    expect(replacementActions(awaitingUnit)).toEqual([
      { type: 'LocationChanged', primary: false },
      { type: 'ReturnRecorded', primary: false },
      { type: 'UnitReserved', primary: true },
    ])
    expect(replacementActions(ready)).toEqual([
      { type: 'ReturnRecorded', primary: false },
      { type: 'HandedToCustomer', primary: true },
    ])
    expect(replacementActions({ allowedEvents: ['LocationChanged'] })).toEqual([{ type: 'LocationChanged', primary: false }])
    expect(replacementActions({ allowedEvents: [] })).toEqual([])
  })

  it('makes the return the primary action only while nothing else moves the replacement on', () => {
    expect(replacementActions(awaitingReturn)).toEqual([
      { type: 'LocationChanged', primary: false },
      { type: 'ReturnRecorded', primary: true },
    ])
    expect(replacementActions(mismatched)).toEqual([
      { type: 'LocationChanged', primary: false },
      { type: 'ReturnAccepted', primary: true },
    ])
    // A claims agent gets no reviewer-only event: nothing of theirs moves a mismatched replacement on.
    expect(replacementActions({ allowedEvents: ['LocationChanged', 'Moved', 'Correction'] })).toEqual([{ type: 'LocationChanged', primary: false }])
  })

  it('asks to confirm the return at hand-over only under AtCollection while the product is not back', () => {
    expect(needsReturnConfirmation({ returnRule: 'AtCollection', returnState: 'NotYetReturned' })).toBe(true)
    expect(needsReturnConfirmation({ returnRule: 'AtCollection', returnState: 'Returned' })).toBe(false)
    expect(needsReturnConfirmation({ returnRule: 'AtCollection', returnState: 'HeldByRepairCentre' })).toBe(false)
    expect(needsReturnConfirmation({ returnRule: 'AtCollection', returnState: 'Accepted' })).toBe(false)
    expect(needsReturnConfirmation({ returnRule: 'BeforeReservation', returnState: 'Returned' })).toBe(false)
  })

  it('words the policy basis and the event origin', () => {
    expect(replacementPolicyBasis(remedy, awaitingUnit)).toBe('AUR-WP v2 · Same model or approved equivalent · Returned at collection')
    expect(replacementPolicyBasis({ policyVersion: { policyCode: 'BOR-WP', version: 1 } }, { productRule: 'SameModel', returnRule: 'BeforeReservation' })).toBe(
      'BOR-WP v1 · Same model only · Returned before reservation',
    )
    expect(replacementEventOrigin(ready.events[1]!)).toMatch(/^reported by phone .+ · recorded by \[AGENT\] .+$/)
    expect(replacementEventOrigin(suggested)).toMatch(/^recorded by the system .+$/)
  })
})

describe('ReplacementCard', () => {
  it('shows the status, the location with its staff contact, the policy basis and the allowed actions', async () => {
    renderTab(['claims-agent'])

    const card = await replacementCard()
    expect(card.getByText('Awaiting replacement unit')).toBeInTheDocument()
    expect(card.getByText('AUR-WP v2 · Same model or approved equivalent · Returned at collection')).toBeInTheDocument()
    const location = within(card.getByRole('region', { name: 'Location' }))
    expect(location.getByText('North Service Point')).toBeInTheDocument()
    expect(location.getByText('1 Service Street')).toBeInTheDocument()
    expect(location.getByText('[LOCATION CONTACT]')).toBeInTheDocument()
    expect(actionNames()).toEqual(['Change location', 'Record return', 'Reserve unit'])
    expect(screen.getByRole('button', { name: 'Reserve unit' }).className).toMatch(/primary/)
    expect(screen.getByRole('button', { name: 'Change location' }).className).not.toMatch(/primary/)
    expect(screen.queryByRole('heading', { name: 'Completion', level: 2 })).not.toBeInTheDocument()
    expect(screen.queryByText('AI')).not.toBeInTheDocument()
  })

  it('shows the reserved unit and offers the hand-over when ready', async () => {
    detail = approvedClaim(ready)
    renderTab(['claims-reviewer'])

    const card = await replacementCard()
    expect(card.getByText('Ready for collection')).toBeInTheDocument()
    expect(within(card.getByRole('region', { name: 'Unit' })).getByText('AT10-9001')).toBeInTheDocument()
    expect(actionNames()).toEqual(['Record return', 'Record hand-over to customer'])
    expect(screen.getByRole('button', { name: 'Record hand-over to customer' }).className).toMatch(/primary/)
  })

  it('shows the replacement events with system and staff entries', async () => {
    detail = approvedClaim(ready)
    renderTab(['claims-agent'])

    const timeline = within(await screen.findByRole('list', { name: 'Replacement events' }))
    expect(timeline.getByText('Location suggested')).toBeInTheDocument()
    expect(timeline.getByText('SYSTEM')).toBeInTheDocument()
    expect(timeline.getByText('Unit reserved')).toBeInTheDocument()
    expect(timeline.getByText(/^reported by phone .* · recorded by \[AGENT\]/)).toBeInTheDocument()
    expect(timeline.getByText('Awaiting replacement unit → Ready for collection')).toBeInTheDocument()
    expect(timeline.getByText('Unit put aside at the counter.')).toBeInTheDocument()
  })

  it('shows the corrected entry struck through, linked to its correction', async () => {
    detail = approvedClaim({
      ...ready,
      events: [
        ...ready.events,
        { seq: 3, type: 'Correction', reportedAt: '2026-10-21T10:00:00Z', recordedAt: '2026-10-21T10:05:00Z', source: 'Email', actor: '[REVIEWER]', correctsSeq: 2 },
      ],
    })
    renderTab(['auditor'])

    const timeline = within(await screen.findByRole('list', { name: 'Replacement events' }))
    expect(timeline.getByText('Unit reserved').className).toMatch(/corrected/)
    expect(timeline.getByRole('link', { name: 'entry 3' })).toHaveAttribute('href', '#replacement-event-3')
    expect(timeline.getByRole('link', { name: 'Entry 2 · Unit reserved' })).toHaveAttribute('href', '#replacement-event-2')
  })

  it('shows auditors every replacement detail read-only', async () => {
    detail = approvedClaim(ready)
    renderTab(['auditor'])

    const card = await replacementCard()
    expect(card.getByText('North Service Point')).toBeInTheDocument()
    expect(card.getByText('AT10-9001')).toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Replacement actions' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Record hand-over to customer' })).not.toBeInTheDocument()
    expect(screen.getByRole('list', { name: 'Replacement events' })).toBeInTheDocument()
  })

  it('shows the return line before the product is back', async () => {
    detail = approvedClaim(awaitingReturn)
    renderTab(['claims-agent'])

    const card = await replacementCard()
    expect(card.getByText('Awaiting return')).toBeInTheDocument()
    expect(card.getByText('AUR-WP v2 · Same model only · Returned before reservation')).toBeInTheDocument()
    const line = within(card.getByRole('region', { name: 'Return' }))
    expect(line.getByText('Not yet returned')).toBeInTheDocument()
    expect(line.queryByText('Serial seen')).not.toBeInTheDocument()
    expect(actionNames()).toEqual(['Change location', 'Record return'])
    expect(screen.getByRole('button', { name: 'Record return' }).className).toMatch(/primary/)
  })

  it('flags a returned serial that does not match the claim with a warn badge and the claim serial', async () => {
    detail = approvedClaim(mismatched)
    renderTab(['claims-reviewer'])

    const line = within((await replacementCard()).getByRole('region', { name: 'Return' }))
    expect(line.getByText('Returned product does not match').className).toMatch(/warn/)
    expect(line.getByText('AT10-7777')).toBeInTheDocument()
    expect(line.getByText('Does not match the claim').className).toMatch(/warn/)
    expect(line.getByText('AT10-0001')).toBeInTheDocument()
    expect(actionNames()).toEqual(['Change location', 'Accept return'])
    expect(screen.getByRole('button', { name: 'Accept return' }).className).toMatch(/primary/)
  })

  it('keeps the mismatch badge on the serial after a reviewer accepted the return', async () => {
    detail = approvedClaim({ ...mismatched, status: 'AwaitingUnit', returnState: 'Accepted', allowedEvents: ['LocationChanged', 'UnitReserved'] })
    renderTab(['claims-agent'])

    const line = within((await replacementCard()).getByRole('region', { name: 'Return' }))
    expect(line.getByText('Return accepted by reviewer')).toBeInTheDocument()
    expect(line.getByText('Does not match the claim')).toBeInTheDocument()
    expect(actionNames()).toEqual(['Change location', 'Reserve unit'])
  })

  it('names the repair centre holding the faulty product and offers no return', async () => {
    detail = { ...approvedClaim(held), repair: holdingRepair }
    renderTab(['claims-agent'])

    const line = within((await replacementCard()).getByRole('region', { name: 'Return' }))
    expect(line.getByText('Held by repair centre')).toBeInTheDocument()
    expect(line.getByText('Held by repair centre — Central Repair Workshop')).toBeInTheDocument()
    expect(line.getByText('Handed to the repair centre on')).toBeInTheDocument()
    expect(line.queryByText('Serial seen')).not.toBeInTheDocument()
    expect(actionNames()).toEqual(['Change location', 'Reserve unit'])
  })

  it('focuses the card when opened from a replacement check in the review queue', async () => {
    detail = approvedClaim(mismatched)
    renderTab(['claims-reviewer'], '?replacement')

    const heading = await screen.findByRole('heading', { name: 'Replacement', level: 2 })
    await vi.waitFor(() => expect(heading.closest('[tabindex="-1"]')).toHaveFocus())
  })

  it('shows no replacement card when the claim has no replacement', async () => {
    detail = approvedClaim(undefined)
    renderTab(['claims-agent'])

    expect(await screen.findByRole('heading', { name: 'Remedy', level: 2 })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Replacement', level: 2 })).not.toBeInTheDocument()
  })
})

describe('Replacement event dialogs', () => {
  it('reserves a unit of the claimed model with If-Match and the serial', async () => {
    onEvent(() => HttpResponse.json(ready as object, { status: 201 }))
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Reserve unit')
    expect(dialog.getByText(/Model: Aurora Tab 10 \(AUR-TAB10\)/)).toBeInTheDocument()
    expect(dialog.getByText(/The customer is notified/)).toBeInTheDocument()
    const submit = dialog.getByRole('button', { name: 'Reserve unit' })
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'Phone')
    expect(submit).toBeDisabled()
    await user.type(dialog.getByRole('textbox', { name: /Serial number/ }), ' AT10-9001 ')

    detail = approvedClaim(ready)
    await user.click(submit)

    expect(await screen.findByText('Replacement update recorded')).toBeInTheDocument()
    expect(events).toHaveLength(1)
    expect(events[0]!.ifMatch).toBe('"x-1"')
    expect(events[0]!.body).toEqual({
      type: 'UnitReserved',
      reportedAt: expect.stringMatching(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:00\.000Z$/),
      source: 'Phone',
      serialNumber: 'AT10-9001',
    })
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await vi.waitFor(() => expect(detailRequests).toBe(2))
    expect(await screen.findByText('Ready for collection')).toBeInTheDocument()
  })

  it('names the approved model when a reviewer approved an equivalent', async () => {
    detail = approvedClaim({
      ...awaitingUnit,
      approvedModel: { productId: 'dddddddd-0000-0000-0000-000000000011', modelCode: 'AUR-TAB11', name: 'Aurora Tab 11', category: 'tablet', claimValue: 520 },
    })
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Reserve unit')
    expect(dialog.getByText(/Model: Aurora Tab 11 \(AUR-TAB11\) \(approved equivalent\)/)).toBeInTheDocument()
  })

  it('changes the location with a reason, listing only the other locations', async () => {
    onEvent(() => HttpResponse.json({ ...awaitingUnit, activeLocation: southLocation } as object, { status: 201 }))
    const user = renderTab(['claims-reviewer'])

    const dialog = await openDialog(user, 'Change location')
    const locationSelect = dialog.getByRole('combobox', { name: /New location/ })
    await vi.waitFor(() => expect(within(locationSelect).getByRole('option', { name: /South Service Point/ })).toBeInTheDocument())
    expect(within(locationSelect).queryByRole('option', { name: /North Service Point/ })).not.toBeInTheDocument()

    await user.selectOptions(locationSelect, southLocation.id)
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'Email')
    const submit = dialog.getByRole('button', { name: 'Change location' })
    await user.type(dialog.getByRole('textbox', { name: /Reason/ }), 'Short')
    expect(submit).toBeDisabled()
    await user.type(dialog.getByRole('textbox', { name: /Reason/ }), ' — the customer lives in the south.')
    await user.click(submit)

    await vi.waitFor(() => expect(events).toHaveLength(1))
    expect(events[0]!.ifMatch).toBe('"x-1"')
    expect(Object.keys(events[0]!.body).sort()).toEqual(['locationId', 'reason', 'reportedAt', 'source', 'type'])
    expect(events[0]!.body).toMatchObject({
      type: 'LocationChanged',
      source: 'Email',
      locationId: southLocation.id,
      reason: 'Short — the customer lives in the south.',
    })
  })

  it('requires "Faulty product returned" for a hand-over under AtCollection', async () => {
    detail = approvedClaim(ready)
    onEvent(() => HttpResponse.json({ ...ready, status: 'HandedToCustomer', allowedEvents: [] } as object, { status: 201 }))
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Record hand-over to customer')
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'InPerson')
    const date = dialog.getByLabelText(/Handed to customer on/)
    await user.clear(date)
    await user.type(date, '2026-10-22')
    const submit = dialog.getByRole('button', { name: 'Record hand-over to customer' })
    expect(submit).toBeDisabled()
    await user.click(dialog.getByRole('checkbox', { name: /Faulty product returned/ }))
    await user.click(submit)

    await vi.waitFor(() => expect(events).toHaveLength(1))
    expect(events[0]!.ifMatch).toBe('"x-2"')
    expect(Object.keys(events[0]!.body).sort()).toEqual(['handedOverOn', 'reportedAt', 'returnConfirmed', 'source', 'type'])
    expect(events[0]!.body).toMatchObject({ type: 'HandedToCustomer', source: 'InPerson', handedOverOn: '2026-10-22', returnConfirmed: true })
  })

  it('asks for no return confirmation when the product is already back', async () => {
    detail = approvedClaim({ ...ready, returnState: 'Returned' })
    onEvent(() => HttpResponse.json({ ...ready, status: 'HandedToCustomer', allowedEvents: [] } as object, { status: 201 }))
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Record hand-over to customer')
    expect(dialog.queryByRole('checkbox')).not.toBeInTheDocument()
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'InPerson')
    await user.click(dialog.getByRole('button', { name: 'Record hand-over to customer' }))

    await vi.waitFor(() => expect(events).toHaveLength(1))
    expect(events[0]!.body).not.toHaveProperty('returnConfirmed')
  })

  it('records the return with the date and the serial seen, with If-Match', async () => {
    detail = approvedClaim(awaitingReturn)
    onEvent(() => HttpResponse.json({ ...awaitingReturn, status: 'AwaitingUnit', returnState: 'Returned' } as object, { status: 201 }))
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Record return')
    expect(dialog.getByText(/a claims reviewer must accept the return/)).toBeInTheDocument()
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'InPerson')
    const date = dialog.getByLabelText(/Returned on/)
    await user.clear(date)
    await user.type(date, '2026-10-21')
    const submit = dialog.getByRole('button', { name: 'Record return' })
    expect(submit).toBeDisabled()
    await user.type(dialog.getByRole('textbox', { name: /Serial number on the returned product/ }), ' AT10-0001 ')
    await user.click(submit)

    await vi.waitFor(() => expect(events).toHaveLength(1))
    expect(events[0]!.ifMatch).toBe('"x-3"')
    expect(Object.keys(events[0]!.body).sort()).toEqual(['reportedAt', 'returnedOn', 'serialSeen', 'source', 'type'])
    expect(events[0]!.body).toMatchObject({ type: 'ReturnRecorded', source: 'InPerson', returnedOn: '2026-10-21', serialSeen: 'AT10-0001' })
  })

  it('refuses a return date in the future', async () => {
    detail = approvedClaim(awaitingReturn)
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Record return')
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'InPerson')
    await user.type(dialog.getByRole('textbox', { name: /Serial number on the returned product/ }), 'AT10-0001')
    const date = dialog.getByLabelText(/Returned on/)
    await user.clear(date)
    await user.type(date, '2099-01-01')

    expect(dialog.getByRole('button', { name: 'Record return' })).toBeDisabled()
  })

  it('shows the API error next to the serial seen', async () => {
    detail = approvedClaim(awaitingReturn)
    onEvent(() =>
      HttpResponse.json(
        { title: 'Validation failed', status: 400, errors: { serialSeen: ['The serial seen on the returned product is required.'] } },
        { status: 400 },
      ),
    )
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Record return')
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'InPerson')
    await user.type(dialog.getByRole('textbox', { name: /Serial number on the returned product/ }), 'AT10-0001')
    await user.click(dialog.getByRole('button', { name: 'Record return' }))

    expect(await dialog.findByText('The serial seen on the returned product is required.')).toBeInTheDocument()
  })

  it('lets a reviewer accept a mismatched return with a reason, showing both serials', async () => {
    detail = approvedClaim(mismatched)
    onEvent(() => HttpResponse.json({ ...mismatched, status: 'AwaitingUnit', returnState: 'Accepted' } as object, { status: 201 }))
    const user = renderTab(['claims-reviewer'])

    const dialog = await openDialog(user, 'Accept return')
    expect(dialog.getByText('AT10-7777')).toBeInTheDocument()
    expect(dialog.getByText('AT10-0001')).toBeInTheDocument()
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'Email')
    const submit = dialog.getByRole('button', { name: 'Accept return' })
    await user.type(dialog.getByRole('textbox', { name: /Reason/ }), 'Too short')
    expect(submit).toBeDisabled()
    await user.type(dialog.getByRole('textbox', { name: /Reason/ }), ' — the label was replaced in a 2025 repair.')
    await user.click(submit)

    await vi.waitFor(() => expect(events).toHaveLength(1))
    expect(events[0]!.ifMatch).toBe('"x-4"')
    expect(Object.keys(events[0]!.body).sort()).toEqual(['reason', 'reportedAt', 'source', 'type'])
    expect(events[0]!.body).toMatchObject({ type: 'ReturnAccepted', source: 'Email', reason: 'Too short — the label was replaced in a 2025 repair.' })
  })

  it('shows the self-review refusal of the API when the reviewer submitted the claim', async () => {
    detail = approvedClaim(mismatched)
    const message = 'You submitted this claim; another reviewer must record this decision.'
    onEvent(() => HttpResponse.json({ title: 'Forbidden', status: 403, detail: message }, { status: 403 }))
    const user = renderTab(['claims-reviewer'])

    const dialog = await openDialog(user, 'Accept return')
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'Email')
    await user.type(dialog.getByRole('textbox', { name: /Reason/ }), 'The label was replaced in a 2025 repair.')
    await user.click(dialog.getByRole('button', { name: 'Accept return' }))

    expect(await dialog.findByRole('alert')).toHaveTextContent(message)
  })

  it('rejects a reported time in the future', async () => {
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Reserve unit')
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'Phone')
    await user.type(dialog.getByRole('textbox', { name: /Serial number/ }), 'AT10-9001')
    const reported = dialog.getByLabelText(/Reported/)
    await user.clear(reported)
    await user.type(reported, '2099-01-01T10:00')

    expect(dialog.getByText('The reported time cannot be in the future.')).toBeInTheDocument()
    expect(dialog.getByRole('button', { name: 'Reserve unit' })).toBeDisabled()
  })

  it('shows a field error from the API next to the serial', async () => {
    onEvent(() =>
      HttpResponse.json(
        { title: 'Validation failed', status: 400, errors: { serialNumber: ['The serial is already handed out on another claim.'] } },
        { status: 400 },
      ),
    )
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Reserve unit')
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'Phone')
    await user.type(dialog.getByRole('textbox', { name: /Serial number/ }), 'AT10-0002')
    await user.click(dialog.getByRole('button', { name: 'Reserve unit' }))

    expect(await dialog.findByText('The serial is already handed out on another claim.')).toBeInTheDocument()
    expect(dialog.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('shows the API message on 409 with Reload', async () => {
    const message = 'This replacement is Ready for collection; allowed: Record hand-over to customer.'
    onEvent(() => HttpResponse.json({ title: 'Conflict', status: 409, detail: message }, { status: 409 }))
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Reserve unit')
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'Phone')
    await user.type(dialog.getByRole('textbox', { name: /Serial number/ }), 'AT10-9001')
    await user.click(dialog.getByRole('button', { name: 'Reserve unit' }))

    const alert = await dialog.findByRole('alert')
    expect(alert).toHaveTextContent(message)
    expect(within(alert).getByRole('button', { name: 'Reload' })).toBeInTheDocument()
  })

  it('shows the 403 message', async () => {
    onEvent(() => HttpResponse.json({ title: 'Forbidden', status: 403 }, { status: 403 }))
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Reserve unit')
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'Phone')
    await user.type(dialog.getByRole('textbox', { name: /Serial number/ }), 'AT10-9001')
    await user.click(dialog.getByRole('button', { name: 'Reserve unit' }))

    expect(await dialog.findByRole('alert')).toHaveTextContent('You are not allowed to record this update. Ask a claims reviewer.')
  })

  it('shows the 412 message, reloads and resubmits with the new ETag', async () => {
    let attempt = 0
    onEvent(() =>
      ++attempt === 1
        ? HttpResponse.json({ title: 'Precondition failed', status: 412 }, { status: 412 })
        : HttpResponse.json(ready as object, { status: 201 }),
    )
    const user = renderTab(['claims-agent'])

    const dialog = await openDialog(user, 'Reserve unit')
    await user.selectOptions(dialog.getByRole('combobox', { name: /Source/ }), 'Phone')
    await user.type(dialog.getByRole('textbox', { name: /Serial number/ }), 'AT10-9001')
    await user.click(dialog.getByRole('button', { name: 'Reserve unit' }))

    const alert = await dialog.findByRole('alert')
    expect(alert).toHaveTextContent('This replacement changed since you opened it. Reload to see the latest status.')

    detail = approvedClaim({ ...awaitingUnit, etag: '"x-9"' })
    await user.click(within(alert).getByRole('button', { name: 'Reload' }))
    await vi.waitFor(() => expect(dialog.queryByRole('alert')).not.toBeInTheDocument())
    expect(dialog.getByRole('textbox', { name: /Serial number/ })).toHaveValue('AT10-9001')

    await user.click(dialog.getByRole('button', { name: 'Reserve unit' }))
    await vi.waitFor(() => expect(events.map((event) => event.ifMatch)).toEqual(['"x-1"', '"x-9"']))
  })
})
