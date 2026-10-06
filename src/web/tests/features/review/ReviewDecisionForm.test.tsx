import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { ReviewDecisionForm, type ReviewDecisionFormProps } from '../../../src/features/review/ReviewDecisionForm'
import type { ClaimDetail, ReviewDecisionKind } from '../../../src/features/review/reviewDecision'
import { aiExplanation, claimId, reviewClaim } from '../../support/reviewClaims'

interface Received {
  ifMatch: string | null
  body: Record<string, unknown>
}

let received: Received[] = []
const server = setupServer()

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
beforeEach(() => {
  received = []
})
afterEach(() => server.resetHandlers())
afterAll(() => server.close())

function onDecision(response: () => Response) {
  server.use(
    http.post('*/api/claims/:claimId/review-decisions', async ({ request }) => {
      received.push({ ifMatch: request.headers.get('If-Match'), body: (await request.json()) as Record<string, unknown> })
      return response()
    }),
  )
}

const decided = (decision: ReviewDecisionKind, overridesAi = false) =>
  HttpResponse.json(
    { id: '33333333-3333-4333-8333-333333333333', decision, overridesAi, reviewerName: 'Riley Reviewer', decidedAt: '2026-10-06T09:00:00Z' },
    { status: 201 },
  )

const problem = (status: number, body: Record<string, unknown> = {}) =>
  HttpResponse.json({ status, title: 'Problem', ...body }, { status, headers: { 'Content-Type': 'application/problem+json' } })

function renderForm(claim: ClaimDetail, decision: ReviewDecisionKind, props: Partial<ReviewDecisionFormProps> = {}) {
  const handlers = {
    onClose: vi.fn(),
    onDecided: vi.fn(),
    onSelfReviewRefused: vi.fn(),
    onAlreadyDecided: vi.fn(),
    onReload: vi.fn(() => Promise.resolve()),
    ...props,
  }
  const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } })
  render(
    <QueryClientProvider client={client}>
      <ReviewDecisionForm claim={claim} etag={'"42"'} decision={decision} {...handlers} />
    </QueryClientProvider>,
  )
  return { user: userEvent.setup(), ...handlers }
}

const messageField = () => screen.getByRole('textbox', { name: /Message to the claimant/ })
const justificationField = () => screen.queryByRole('textbox', { name: /Internal reason — never shown to the claimant/ })
const confirm = (name: string) => screen.getByRole('button', { name })

describe('ReviewDecisionForm', () => {
  it('approves in agreement with the AI using the pre-filled message and If-Match', async () => {
    onDecision(() => decided('Approve'))
    const { user, onDecided } = renderForm(reviewClaim({ decision: 'APPROVE' }), 'Approve')

    expect(screen.getByRole('dialog', { name: 'Approve claim' })).toBeInTheDocument()
    expect(messageField()).toHaveValue(aiExplanation)
    expect(justificationField()).not.toBeInTheDocument()
    expect(screen.queryByText(/AI recommends/)).not.toBeInTheDocument()

    await user.click(confirm('Approve claim'))

    await waitFor(() => expect(onDecided).toHaveBeenCalled())
    expect(received).toEqual([{ ifMatch: '"42"', body: { decision: 'Approve', claimantExplanation: aiExplanation } }])
  })

  it('requires the claimant message and internal reason when overriding the AI', async () => {
    onDecision(() => decided('Reject', true))
    const { user } = renderForm(reviewClaim({ decision: 'APPROVE' }), 'Reject')

    expect(screen.getByRole('dialog', { name: 'Override AI recommendation' })).toBeInTheDocument()
    expect(screen.getByText(/AI recommends/)).toHaveTextContent(
      'AI recommends Approve. You are choosing Reject. This is recorded as a human decision with your reason, linked to the decision trace.',
    )
    expect(messageField()).toHaveValue('')
    expect(confirm('Reject claim')).toBeDisabled()

    await user.type(messageField(), 'The damage is not covered by your warranty.')
    expect(confirm('Reject claim')).toBeDisabled()
    await user.type(justificationField()!, 'Too short')
    expect(confirm('Reject claim')).toBeDisabled()
    await user.type(justificationField()!, ' — photos show impact damage')
    expect(confirm('Reject claim')).toBeEnabled()

    await user.click(confirm('Reject claim'))
    await waitFor(() => expect(received).toHaveLength(1))
    expect(received[0]!.body).toEqual({
      decision: 'Reject',
      claimantExplanation: 'The damage is not covered by your warranty.',
      justification: 'Too short — photos show impact damage',
    })
  })

  it('does not pre-fill the message when the AI text failed CLAIMANT_TEXT_SAFE', () => {
    renderForm(reviewClaim({ decision: 'APPROVE', textSafe: false }), 'Approve')
    expect(messageField()).toHaveValue('')
  })

  it('shows no override wording and no pre-fill without a valid AI decision', () => {
    renderForm(reviewClaim({ decision: 'HUMAN_REVIEW' }), 'Approve')

    expect(screen.getByRole('dialog', { name: 'Approve claim' })).toBeInTheDocument()
    expect(messageField()).toHaveValue('')
    expect(justificationField()).not.toBeInTheDocument()
    expect(screen.queryByText(/AI recommends/)).not.toBeInTheDocument()
  })

  it('still requires a reason for a rejection without an AI decision, without override wording', () => {
    renderForm(reviewClaim({ decision: 'REQUEST_MORE_INFORMATION' }), 'Reject')

    expect(screen.getByRole('dialog', { name: 'Reject claim' })).toBeInTheDocument()
    expect(justificationField()).toBeInTheDocument()
    expect(screen.queryByText(/AI recommends/)).not.toBeInTheDocument()
  })

  it('requests information with picked items, no claimant message and the return-to-queue notice', async () => {
    onDecision(() => decided('RequestInformation'))
    const { user } = renderForm(reviewClaim({ decision: 'HUMAN_REVIEW' }), 'RequestInformation')

    expect(screen.getByRole('dialog', { name: 'Request more information' })).toBeInTheDocument()
    expect(screen.queryByRole('textbox', { name: /Message to the claimant/ })).not.toBeInTheDocument()
    expect(screen.getByText('After the customer responds, this claim returns to the review queue.')).toBeInTheDocument()
    expect(confirm('Request information')).toBeDisabled()

    await user.click(screen.getByRole('checkbox', { name: 'Photo of the serial number label' }))
    await user.click(confirm('Request information'))

    await waitFor(() => expect(received).toHaveLength(1))
    expect(received[0]!.body).toEqual({
      decision: 'RequestInformation',
      requestedItems: [{ item: 'PHOTO_OF_SERIAL_LABEL', reason: 'Please upload a photo of the label showing the serial number.' }],
    })
  })

  it('shows a 400 disclosure-term error on the message field', async () => {
    onDecision(() =>
      problem(400, { errors: { claimantExplanation: ['The message mentions "fraud", which must not be shown to the claimant.'] } }),
    )
    const { user } = renderForm(reviewClaim({ decision: 'APPROVE' }), 'Approve')

    await user.click(confirm('Approve claim'))

    expect(await screen.findByText(/mentions "fraud"/)).toBeInTheDocument()
    expect(messageField()).toHaveAttribute('aria-invalid', 'true')
    expect(messageField()).toHaveAccessibleDescription(expect.stringContaining('fraud'))
  })

  it('shows 409 and 412 conflicts and reports a 403 self-review refusal', async () => {
    onDecision(() => problem(409))
    const { user, onAlreadyDecided, onReload, onSelfReviewRefused } = renderForm(reviewClaim(), 'Approve')

    await user.click(confirm('Approve claim'))
    expect(await screen.findByText('This claim has already been decided.')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Refresh' }))
    expect(onAlreadyDecided).toHaveBeenCalled()

    server.use(http.post('*/api/claims/:claimId/review-decisions', () => problem(412)))
    await user.click(confirm('Approve claim'))
    expect(await screen.findByText('The claim changed since you opened it — reload.')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Reload' }))
    expect(onReload).toHaveBeenCalled()
    await waitFor(() => expect(screen.queryByText(/changed since you opened it/)).not.toBeInTheDocument())

    server.use(http.post('*/api/claims/:claimId/review-decisions', () => problem(403)))
    await user.click(confirm('Approve claim'))
    await waitFor(() => expect(onSelfReviewRefused).toHaveBeenCalled())
  })

  it('posts to the claim being reviewed', async () => {
    let path = ''
    server.use(
      http.post('*/api/claims/:claimId/review-decisions', ({ params }) => {
        path = String(params.claimId)
        return decided('Approve')
      }),
    )
    const { user, onDecided } = renderForm(reviewClaim(), 'Approve')
    await user.click(confirm('Approve claim'))
    await waitFor(() => expect(onDecided).toHaveBeenCalled())
    expect(path).toBe(claimId)
  })
})
