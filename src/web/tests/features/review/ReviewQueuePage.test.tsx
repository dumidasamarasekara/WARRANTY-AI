import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { RouterProvider, createMemoryRouter } from 'react-router'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { ReviewQueuePage } from '../../../src/features/review/ReviewQueuePage'
import type { ReviewQueueItem } from '../../../src/features/review/reviewDecision'
import { ToastProvider } from '../../../src/shared/ui'
import { aiExplanation, claimId, queueItem, reviewClaim } from '../../support/reviewClaims'

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    isLoading: false,
    user: { access_token: 'header.e30.signature', profile: { sub: 'u-2', name: 'Riley Reviewer' } },
    signoutRedirect: vi.fn(),
  }),
  hasAuthParams: () => false,
}))

const now = new Date('2026-10-06T10:00:00Z')

const server = setupServer(
  http.get('*/api/me', () =>
    HttpResponse.json({ sub: 'u-2', name: 'Riley Reviewer', tenantDisplayName: 'Aurora Electronics', tenantCurrency: 'USD', roles: ['claims-reviewer'] }),
  ),
  http.get('*/api/claims/:claimId', () => HttpResponse.json(reviewClaim(), { headers: { ETag: '"7"' } })),
)

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
afterEach(() => server.resetHandlers())
afterAll(() => server.close())

function queueResponds(items: () => ReviewQueueItem[]) {
  server.use(http.get('*/api/review-queue', () => HttpResponse.json(items())))
}

function renderPage(path = '/staff/review') {
  const router = createMemoryRouter(
    [
      { path: '/staff/review/:claimId?', element: <ReviewQueuePage now={now} /> },
      { path: '/staff/claims/:claimId', element: <h1>Case file</h1> },
    ],
    { initialEntries: [path] },
  )
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  render(
    <QueryClientProvider client={client}>
      <ToastProvider>
        <RouterProvider router={router} />
      </ToastProvider>
    </QueryClientProvider>,
  )
  return userEvent.setup()
}

const decisionActions = () => within(screen.getByRole('group', { name: 'Decision' })).getAllByRole('button')

describe('ReviewQueuePage', () => {
  it('lists escalated claims with reasons by label and opens the oldest in the workspace', async () => {
    queueResponds(() => [queueItem(), queueItem({ claimId: 'b0000000-0000-4000-8000-000000000002', reference: 'Z9Y8X7W6VU', productName: 'Aurora Tab 10' })])
    renderPage()

    expect(await screen.findByText('Claims the AI could not safely decide on its own · Aurora Electronics')).toBeInTheDocument()
    const queue = screen.getByRole('navigation', { name: 'Review queue' })
    const first = within(queue).getByRole('link', { name: /K7M2Q9X4TB/ })
    expect(first).toHaveAttribute('aria-current', 'page')
    expect(first).toHaveTextContent('USD 1,400.00 · Claim value above the automatic approval limit')
    expect(first).toHaveTextContent('Escalated 2 h ago')

    const banner = await screen.findByRole('region', { name: 'Human review required' })
    expect(banner).toHaveTextContent('Claim value above the automatic approval limit')
    expect(banner).toHaveTextContent('You are the control point — the AI recommendation below is advisory.')
    expect(screen.getByText('Pending')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Override AI recommendation…' })).toBeEnabled()
    expect(screen.getByRole('link', { name: 'Open full case file →' })).toHaveAttribute('href', `/staff/claims/${claimId}`)
  })

  it('shows "No AI decision" and no override action when the AI did not decide', async () => {
    queueResponds(() => [queueItem({ aiDecision: undefined })])
    server.use(http.get('*/api/claims/:claimId', () => HttpResponse.json(reviewClaim({ decision: 'HUMAN_REVIEW' }), { headers: { ETag: '"7"' } })))
    renderPage()

    expect(await screen.findByText('No AI decision')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Override AI recommendation…' })).not.toBeInTheDocument()
  })

  it('disables the decision actions on a claim the reviewer submitted', async () => {
    queueResponds(() => [queueItem({ submittedByMe: true })])
    renderPage()

    expect(await screen.findByText('You submitted this claim')).toBeInTheDocument()
    expect(await screen.findByText('Another reviewer must decide this claim')).toBeInTheDocument()
    for (const button of decisionActions()) expect(button).toBeDisabled()
  })

  it('shows the separation-of-duties message after a 403 and disables the actions', async () => {
    queueResponds(() => [queueItem()])
    server.use(
      http.post('*/api/claims/:claimId/review-decisions', () =>
        HttpResponse.json({ status: 403, title: 'Forbidden', detail: 'You submitted this claim; another reviewer must decide it' }, { status: 403 }),
      ),
    )
    const user = renderPage()

    await user.click(await screen.findByRole('button', { name: 'Approve' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Approve claim' }))

    expect(await screen.findByText('Another reviewer must decide this claim')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    for (const button of decisionActions()) expect(button).toBeDisabled()
  })

  it('records a decision with If-Match, confirms it and drops the claim from the queue', async () => {
    let decided = false
    let ifMatch: string | null = null
    queueResponds(() => (decided ? [] : [queueItem()]))
    server.use(
      http.post('*/api/claims/:claimId/review-decisions', ({ request }) => {
        decided = true
        ifMatch = request.headers.get('If-Match')
        return HttpResponse.json(
          { id: 'd', decision: 'Approve', overridesAi: false, claimantExplanation: aiExplanation, reviewerName: 'Riley Reviewer', decidedAt: now.toISOString() },
          { status: 201 },
        )
      }),
    )
    const user = renderPage(`/staff/review/${claimId}`)

    await user.click(await screen.findByRole('button', { name: 'Approve' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Approve claim' }))

    expect(await screen.findByText('Decision recorded')).toBeInTheDocument()
    expect(await screen.findByText('No claims waiting for review')).toBeInTheDocument()
    expect(ifMatch).toBe('"7"')
  })

  it('shows an empty state when nothing is waiting', async () => {
    queueResponds(() => [])
    renderPage()
    expect(await screen.findByText('No claims waiting for review')).toBeInTheDocument()
  })
})
