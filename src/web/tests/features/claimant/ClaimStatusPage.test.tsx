import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { RouterProvider, createMemoryRouter, useLocation } from 'react-router'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from 'vitest'
import { ClaimStatusPage } from '../../../src/features/claimant/ClaimStatusPage'
import { clearClaimantToken, holdClaimantToken } from '../../../src/shared/api/claimantToken'

const server = setupServer()
const reference = 'ABCD234567'
const submittedAt = '2026-10-02T09:41:00Z'
let statusRequests: Request[] = []

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
beforeEach(() => {
  statusRequests = []
  holdClaimantToken(reference, 'claimant-token', new Date(Date.now() + 3_600_000).toISOString())
})
afterEach(() => {
  server.resetHandlers()
  clearClaimantToken()
})
afterAll(() => server.close())

/** Answers the status calls in order; the last answer repeats. */
function respondWith(...views: Array<Record<string, unknown>>) {
  server.use(
    http.get('*/api/public/claims/:reference', ({ request }) => {
      statusRequests.push(request)
      const view = views[Math.min(statusRequests.length - 1, views.length - 1)]
      return HttpResponse.json({ reference, submittedAt, ...view })
    }),
  )
}

function AccessProbe() {
  const location = useLocation()
  return <h1>Access page {location.search}</h1>
}

function renderStatus() {
  const router = createMemoryRouter(
    [
      { path: '/claims/access', element: <AccessProbe /> },
      { path: '/claims/:reference', element: <ClaimStatusPage pollIntervalMs={10} /> },
    ],
    { initialEntries: [`/claims/${reference}`] },
  )
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
}

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

function stepState(label: string) {
  const step = within(screen.getByRole('list', { name: 'Claim progress' }))
    .getAllByRole('listitem')
    .find((item) => item.textContent?.includes(`${label} (`))
  return step?.textContent?.match(/\(([^)]+)\)$/)?.[1]
}

describe('ClaimStatusPage', () => {
  it('polls while the claim is being evaluated and stops at a non-transient status', async () => {
    respondWith(
      { status: 'Submitted' },
      { status: 'UnderEvaluation' },
      {
        status: 'Approved',
        productName: 'Aurora AquaPure W7 Washer',
        outcomeExplanation: 'A control panel replacement is covered at no cost to you.',
      },
    )
    renderStatus()

    expect(await screen.findByText("We're checking your claim")).toBeInTheDocument()
    expect(await screen.findByText('Your claim is approved')).toBeInTheDocument()
    const outcome = screen.getByRole('region', { name: 'Claim outcome' })
    expect(outcome).toHaveTextContent('Aurora AquaPure W7 Washer')
    expect(outcome).toHaveTextContent('A control panel replacement is covered at no cost to you.')
    expect(outcome.parentElement).toHaveAttribute('aria-live', 'polite')
    expect(statusRequests[0]?.headers.get('Authorization')).toBe('Bearer claimant-token')

    const requestsAtFinalStatus = statusRequests.length
    expect(requestsAtFinalStatus).toBe(3)
    await wait(80)
    expect(statusRequests.length).toBe(requestsAtFinalStatus)
  })

  it('completes the stepper and adds the service centre step for an approved claim', async () => {
    respondWith({ status: 'Approved', outcomeExplanation: 'Covered.' })
    renderStatus()

    await screen.findByText('Your claim is approved')
    expect(stepState('Evaluation')).toBe('completed')
    expect(stepState('Decision')).toBe('completed')
    const timeline = screen.getByRole('list', { name: 'What happens next' })
    expect(within(timeline).getByText('Received')).toBeInTheDocument()
    expect(within(timeline).getByText('The service centre will contact you')).toBeInTheDocument()
  })

  it('shows the explanation of a rejected claim without a service centre step', async () => {
    respondWith({ status: 'Rejected', outcomeExplanation: 'Water damage is not covered by your warranty.' })
    renderStatus()

    expect(await screen.findByText('Your claim was not approved')).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Claim outcome' })).toHaveTextContent(
      'Water damage is not covered by your warranty.',
    )
    expect(screen.queryByText('The service centre will contact you')).not.toBeInTheDocument()
  })

  it('lists the requested items and marks the evaluation step as needing attention', async () => {
    respondWith({
      status: 'PendingInformation',
      requestedItems: [
        { item: 'Invoice', reason: 'The invoice photo is unreadable.' },
        { item: 'Serial plate photo', reason: 'We need to confirm the serial number.' },
      ],
    })
    renderStatus()

    expect(await screen.findByText('We need a bit more information')).toBeInTheDocument()
    const outcome = screen.getByRole('region', { name: 'Claim outcome' })
    expect(within(outcome).getByText('Invoice')).toBeInTheDocument()
    expect(outcome).toHaveTextContent('The invoice photo is unreadable.')
    expect(outcome).toHaveTextContent('We need to confirm the serial number.')
    expect(stepState('Information needed')).toBe('needs your attention')
    await wait(50)
    expect(statusRequests.length).toBe(1)
  })

  it('shows "Under review" as the current step for an escalated claim', async () => {
    respondWith({ status: 'UnderReview' })
    renderStatus()

    expect(await screen.findByText(/A member of our team is reviewing your claim/)).toBeInTheDocument()
    expect(stepState('Under review')).toBe('current step')
    expect(stepState('Decision')).toBe('not started')
  })

  it('never displays risk or internal data even if the response carries it', async () => {
    respondWith({
      status: 'Rejected',
      outcomeExplanation: 'The purchase date is outside the warranty period.',
      riskLevel: 'High',
      riskScore: 87,
      fraudIndicators: ['DUPLICATE_SERIAL'],
      justification: 'Internal reviewer note',
    })
    renderStatus()

    await screen.findByText('Your claim was not approved')
    const page = document.body.textContent ?? ''
    expect(page).not.toMatch(/risk|fraud|DUPLICATE_SERIAL|Internal reviewer note|87|High/i)
  })

  it('sends the claimant to the access page when no token is held', async () => {
    clearClaimantToken()
    respondWith({ status: 'Submitted' })
    renderStatus()

    expect(await screen.findByRole('heading', { name: `Access page ?reference=${reference}` })).toBeInTheDocument()
    expect(statusRequests).toHaveLength(0)
  })

  it('sends the claimant to the access page when the token is refused', async () => {
    server.use(
      http.get('*/api/public/claims/:reference', () =>
        HttpResponse.json({ title: 'Unauthorized', status: 401 }, { status: 401 }),
      ),
    )
    renderStatus()

    expect(await screen.findByRole('heading', { name: `Access page ?reference=${reference}` })).toBeInTheDocument()
  })
})
