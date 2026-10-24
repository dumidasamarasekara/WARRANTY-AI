import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { ReviewDecisionForm, type ReviewDecisionFormProps } from '../../../src/features/review/ReviewDecisionForm'
import type { ClaimDetail, ReviewDecisionKind } from '../../../src/features/review/reviewDecision'
import { aiExplanation, claimId, claimWithProtectionPlan, claimWithRemedyPreview, reviewClaim } from '../../support/reviewClaims'

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

  it('requires a reason to reject even in agreement with the AI, keeping the pre-filled message', async () => {
    onDecision(() => decided('Reject'))
    const { user } = renderForm(reviewClaim({ decision: 'REJECT' }), 'Reject')

    expect(screen.getByRole('dialog', { name: 'Reject claim' })).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Override AI recommendation' })).not.toBeInTheDocument()
    expect(messageField()).toHaveValue(aiExplanation)
    expect(justificationField()).toBeInTheDocument()
    expect(confirm('Reject claim')).toBeDisabled()

    await user.type(justificationField()!, 'Liquid damage visible on photo 2')
    expect(confirm('Reject claim')).toBeEnabled()
    await user.click(confirm('Reject claim'))

    await waitFor(() => expect(received).toHaveLength(1))
    expect(received[0]!.body).toEqual({
      decision: 'Reject',
      claimantExplanation: aiExplanation,
      justification: 'Liquid damage visible on photo 2',
    })
  })

  it('does not pre-fill an approval that overrides an AI rejection and requires the reason', () => {
    renderForm(reviewClaim({ decision: 'REJECT' }), 'Approve')

    expect(screen.getByRole('dialog', { name: 'Override AI recommendation' })).toBeInTheDocument()
    expect(screen.getByText(/AI recommends/)).toHaveTextContent('AI recommends Reject. You are choosing Approve.')
    expect(messageField()).toHaveValue('')
    expect(justificationField()).toBeInTheDocument()
    expect(confirm('Approve claim')).toBeDisabled()
  })

  it('requires a claimant message of at least 20 characters to approve when nothing is pre-filled', async () => {
    onDecision(() => decided('Approve'))
    const { user } = renderForm(reviewClaim({ decision: 'HUMAN_REVIEW' }), 'Approve')

    expect(confirm('Approve claim')).toBeDisabled()
    await user.type(messageField(), 'Approved.')
    expect(confirm('Approve claim')).toBeDisabled()
    await user.type(messageField(), ' Your repair is covered.')
    expect(confirm('Approve claim')).toBeEnabled()

    await user.click(confirm('Approve claim'))
    await waitFor(() => expect(received).toHaveLength(1))
    expect(received[0]!.body).toEqual({ decision: 'Approve', claimantExplanation: 'Approved. Your repair is covered.' })
  })

  it('treats an invalid AI recommendation as no AI decision: no pre-fill and no override wording', () => {
    renderForm(reviewClaim({ decision: 'APPROVE', isValid: false }), 'Reject')

    expect(screen.getByRole('dialog', { name: 'Reject claim' })).toBeInTheDocument()
    expect(messageField()).toHaveValue('')
    expect(screen.queryByText(/AI recommends/)).not.toBeInTheDocument()
    expect(justificationField()).toBeInTheDocument()
  })

  it('does not pre-fill the message when the AI text failed CLAIMANT_TEXT_SAFE', () => {
    renderForm(reviewClaim({ decision: 'APPROVE', textSafe: false }), 'Approve')
    expect(messageField()).toHaveValue('')
  })

  it('does not pre-fill the message when the CLAIMANT_TEXT_SAFE check did not run', () => {
    renderForm(reviewClaim({ decision: 'APPROVE', textSafe: null }), 'Approve')
    expect(messageField()).toHaveValue('')
  })

  it('has no claimant message for an information request even against a valid AI decision', () => {
    renderForm(reviewClaim({ decision: 'APPROVE' }), 'RequestInformation')

    expect(screen.getByRole('dialog', { name: 'Request more information' })).toBeInTheDocument()
    expect(screen.queryByRole('textbox', { name: /Message to the claimant/ })).not.toBeInTheDocument()
    expect(screen.queryByText(aiExplanation)).not.toBeInTheDocument()
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

  describe('remedy choice (002)', () => {
    const remedyGroup = () => screen.queryByRole('radiogroup', { name: 'Remedy' })

    it('lists only the allowed remedies and blocks approving until one is chosen', async () => {
      onDecision(() => decided('Approve'))
      const { user } = renderForm(claimWithRemedyPreview(), 'Approve')

      const group = remedyGroup()!
      const options = within(group).getAllByRole('radio')
      expect(options.map((option) => option.getAttribute('value'))).toEqual(['Replacement', 'Refund'])
      expect(within(group).queryByRole('radio', { name: /Repair/ })).not.toBeInTheDocument()
      for (const option of options) expect(option).not.toBeChecked()
      expect(confirm('Approve claim')).toBeDisabled()

      await user.click(within(group).getByRole('radio', { name: /Refund/ }))
      expect(confirm('Approve claim')).toBeEnabled()
      await user.click(confirm('Approve claim'))

      await waitFor(() => expect(received).toHaveLength(1))
      expect(received[0]!.body).toEqual({ decision: 'Approve', claimantExplanation: aiExplanation, remedy: 'Refund' })
    })

    it('pre-selects the policy recommendation and sends it', async () => {
      onDecision(() => decided('Approve'))
      const { user } = renderForm(
        claimWithRemedyPreview({ scenario: 'CoveredDefect', allowedRemedies: ['Repair'], recommendedRemedy: 'Repair', needsChoice: false, tableRow: 'coveredDefect' }),
        'Approve',
      )

      const repair = within(remedyGroup()!).getByRole('radio', { name: /Repair/ })
      expect(repair).toBeChecked()
      expect(within(remedyGroup()!).getByText('set by policy')).toBeInTheDocument()
      expect(confirm('Approve claim')).toBeEnabled()
      await user.click(confirm('Approve claim'))

      await waitFor(() => expect(received).toHaveLength(1))
      expect(received[0]!.body).toEqual({ decision: 'Approve', claimantExplanation: aiExplanation, remedy: 'Repair' })
    })

    it('shows no remedy choice when rejecting or requesting information', async () => {
      onDecision(() => decided('Reject', true))
      const { user } = renderForm(claimWithRemedyPreview(), 'Reject')

      expect(remedyGroup()).not.toBeInTheDocument()
      await user.type(messageField(), 'The damage is not covered by your warranty.')
      await user.type(justificationField()!, 'Photos show impact damage')
      await user.click(confirm('Reject claim'))

      await waitFor(() => expect(received).toHaveLength(1))
      expect(received[0]!.body).not.toHaveProperty('remedy')
    })

    it('has no remedy choice for an information request', () => {
      renderForm(claimWithRemedyPreview(), 'RequestInformation')
      expect(remedyGroup()).not.toBeInTheDocument()
    })

    it('shows a 400 naming the allowed remedies on the remedy group', async () => {
      onDecision(() => problem(400, { errors: { remedy: ['Choose one of: Replacement, Refund.'] } }))
      const { user } = renderForm(claimWithRemedyPreview(), 'Approve')

      await user.click(within(remedyGroup()!).getByRole('radio', { name: /Replacement/ }))
      await user.click(confirm('Approve claim'))

      expect(await screen.findByText('Choose one of: Replacement, Refund.')).toBeInTheDocument()
      expect(remedyGroup()).toHaveAccessibleDescription('Choose one of: Replacement, Refund.')
    })
  })

  describe('protection plan check (002 FR-036)', () => {
    const planCheck = () => screen.queryByRole('checkbox', { name: 'Protection plan checked' })
    const remedyGroup = () => screen.getByRole('radiogroup', { name: 'Remedy' })

    it('is not offered when the claim states no plan', () => {
      renderForm(claimWithRemedyPreview(), 'Approve')
      expect(planCheck()).not.toBeInTheDocument()
    })

    it('is not offered when rejecting a claim with a plan', () => {
      renderForm(claimWithProtectionPlan(), 'Reject')
      expect(planCheck()).not.toBeInTheDocument()
    })

    it('sends protectionPlanConfirmed false when the reviewer leaves it unticked', async () => {
      onDecision(() => decided('Approve'))
      const { user } = renderForm(claimWithProtectionPlan(), 'Approve')

      expect(planCheck()).not.toBeChecked()
      expect(within(remedyGroup()).getByRole('radio', { name: /Repair/ })).toBeChecked()
      await user.click(confirm('Approve claim'))

      await waitFor(() => expect(received).toHaveLength(1))
      expect(received[0]!.body).toEqual({
        decision: 'Approve',
        claimantExplanation: aiExplanation,
        remedy: 'Repair',
        protectionPlanConfirmed: false,
      })
    })

    it("switches to the plan's remedies when ticked and sends the confirmation", async () => {
      onDecision(() => decided('Approve'))
      const { user } = renderForm(claimWithProtectionPlan(), 'Approve')

      await user.click(planCheck()!)

      const options = within(remedyGroup()).getAllByRole('radio')
      expect(options.map((option) => option.getAttribute('value'))).toEqual(['Replacement', 'Refund'])
      for (const option of options) expect(option).not.toBeChecked()
      expect(confirm('Approve claim')).toBeDisabled()

      await user.click(within(remedyGroup()).getByRole('radio', { name: /Replacement/ }))
      await user.click(confirm('Approve claim'))

      await waitFor(() => expect(received).toHaveLength(1))
      expect(received[0]!.body).toEqual({
        decision: 'Approve',
        claimantExplanation: aiExplanation,
        remedy: 'Replacement',
        protectionPlanConfirmed: true,
      })
    })

    it('restores the policy remedy when unticked again', async () => {
      const { user } = renderForm(claimWithProtectionPlan(), 'Approve')

      await user.click(planCheck()!)
      await user.click(planCheck()!)

      expect(within(remedyGroup()).getAllByRole('radio').map((option) => option.getAttribute('value'))).toEqual(['Repair'])
      expect(within(remedyGroup()).getByRole('radio', { name: /Repair/ })).toBeChecked()
    })
  })})
