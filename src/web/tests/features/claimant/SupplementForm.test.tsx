import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { SubmissionAccepted } from '../../../src/features/claimant/claimSubmission'
import type { RequestedItem } from '../../../src/features/claimant/supplement'
import { SupplementForm } from '../../../src/features/claimant/SupplementForm'
import { file } from '../../support/claimForm'
import { useNodeMultipartClasses } from '../../support/multipart'

const accepted: SubmissionAccepted = { reference: 'K7M2Q9X4TB', status: 'Submitted', round: 2 }
const planRequested: RequestedItem = { item: 'PROTECTION_PLAN_DOCUMENT', reason: 'Please upload your protection plan document.' }
const invoiceRequested: RequestedItem = { item: 'INVOICE', reason: 'Please upload the invoice or receipt for this purchase.' }

beforeEach(async () => {
  await useNodeMultipartClasses()
})

function renderForm(requestedItems: RequestedItem[]) {
  const submit = vi.fn((body: FormData) => {
    void body
    return Promise.resolve(accepted)
  })
  const onSubmitted = vi.fn()
  const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } })
  render(
    <QueryClientProvider client={client}>
      <SupplementForm variant="claimant" requestedItems={requestedItems} submit={submit} onSubmitted={onSubmitted} />
    </QueryClientProvider>,
  )
  return { user: userEvent.setup({ applyAccept: false }), submit, onSubmitted }
}

describe('SupplementForm protection plan document (002 FR-036, FR-037)', () => {
  it('offers the plan document only when the claim requests it', () => {
    renderForm([invoiceRequested])
    expect(screen.queryByLabelText('Protection plan document')).not.toBeInTheDocument()
  })

  it('lists the request and sends the plan document as its own part', async () => {
    const { user, submit, onSubmitted } = renderForm([planRequested])

    expect(screen.getByText('Protection plan document', { selector: 'strong' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Send information' }))
    expect(screen.getByText('Add a note, the invoice, at least one photo or the protection plan document')).toBeInTheDocument()
    expect(submit).not.toHaveBeenCalled()

    await user.upload(screen.getByLabelText('Protection plan document'), file('plan.png', 'image/png'))
    await user.click(screen.getByRole('button', { name: 'Send information' }))

    await waitFor(() => expect(onSubmitted).toHaveBeenCalled())
    expect(onSubmitted.mock.calls[0]![0]).toEqual(accepted)
    const body = submit.mock.calls[0]![0]
    expect((body.get('protectionPlanDocument') as File).name).toBe('plan.png')
    expect(body.has('invoice')).toBe(false)
  })
})
