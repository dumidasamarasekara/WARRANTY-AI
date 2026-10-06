import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { RouterProvider, createMemoryRouter, useLocation } from 'react-router'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { ClaimStatusPage } from '../../../src/features/claimant/ClaimStatusPage'
import { clearClaimantToken, holdClaimantToken } from '../../../src/shared/api/claimantToken'
import { file } from '../../support/claimForm'
import { useNodeMultipartClasses } from '../../support/multipart'

const server = setupServer()
const reference = 'ABCD234567'
const submittedAt = '2026-10-02T09:41:00Z'
let statusRequests: Request[] = []

interface ReceivedSupplement {
  authorization: string | null
  note: FormDataEntryValue | null
  invoice: string[]
  photos: string[]
}
let supplements: ReceivedSupplement[] = []

/** Item codes and claimant-safe reasons, as the API returns them (RequestedItemCatalog). */
const pendingInformation = {
  status: 'PendingInformation',
  requestedItems: [
    { item: 'LEGIBLE_INVOICE', reason: 'Please upload a clearer copy of the invoice so it can be read.' },
    { item: 'PHOTO_OF_SERIAL_LABEL', reason: 'Please upload a photo of the label showing the serial number.' },
  ],
}

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
beforeEach(async () => {
  statusRequests = []
  supplements = []
  holdClaimantToken(reference, 'claimant-token', new Date(Date.now() + 3_600_000).toISOString())
  await useNodeMultipartClasses()
})
afterEach(() => {
  server.resetHandlers()
  clearClaimantToken()
  vi.unstubAllGlobals()
})
afterAll(() => server.close())

const names = (parts: FormDataEntryValue[]) => parts.map((part) => (typeof part === 'string' ? part : part.name))

/** Records each supplement's token and multipart parts, then answers with `response`. */
function onSupplement(response: () => Response) {
  server.use(
    http.post('*/api/public/claims/:reference/supplements', async ({ request }) => {
      const form = await request.formData()
      supplements.push({
        authorization: request.headers.get('Authorization'),
        note: form.get('note'),
        invoice: names(form.getAll('invoice')),
        photos: names(form.getAll('photos')),
      })
      return response()
    }),
  )
}

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

  it('lists the requested items by their labels and marks the evaluation step as needing attention', async () => {
    respondWith(pendingInformation)
    renderStatus()

    expect(await screen.findByText('We need a bit more information')).toBeInTheDocument()
    const requested = within(screen.getByRole('region', { name: 'What we need' }))
    expect(requested.getByText('Clearer copy of the invoice')).toBeInTheDocument()
    expect(requested.getByText('Photo of the serial number label')).toBeInTheDocument()
    expect(requested.getByText('Please upload a clearer copy of the invoice so it can be read.')).toBeInTheDocument()
    expect(requested.getByText('Please upload a photo of the label showing the serial number.')).toBeInTheDocument()
    expect(document.body).not.toHaveTextContent(/LEGIBLE_INVOICE|PHOTO_OF_SERIAL_LABEL/)
    expect(stepState('Information needed')).toBe('needs your attention')
    await wait(50)
    expect(statusRequests.length).toBe(1)
  })

  it('shows the supplement form only while information is needed', async () => {
    respondWith({ status: 'UnderReview' })
    renderStatus()

    await screen.findByText(/A member of our team is reviewing your claim/)
    expect(screen.queryByRole('form', { name: 'Send information' })).not.toBeInTheDocument()
  })

  it('asks for at least a note, the invoice or a photo before sending a supplement', async () => {
    respondWith(pendingInformation)
    const user = userEvent.setup({ applyAccept: false })
    renderStatus()

    await user.click(await screen.findByRole('button', { name: 'Send information' }))

    expect(screen.getByRole('alert')).toHaveTextContent('Add a note, the invoice or at least one photo')
    expect(supplements).toHaveLength(0)

    await user.upload(screen.getByLabelText(/Photos of the product/), file('IMG_0002.HEIC', 'image/heic'))
    expect(screen.getByText('Please send photos as JPEG or PNG')).toBeInTheDocument()
  })

  it('sends the supplement with the claimant token and follows the new evaluation round', async () => {
    respondWith(pendingInformation, { status: 'UnderEvaluation' })
    onSupplement(() => HttpResponse.json({ reference, status: 'Submitted', round: 2 }, { status: 202 }))
    const user = userEvent.setup({ applyAccept: false })
    renderStatus()

    await user.type(await screen.findByRole('textbox', { name: /Anything else/ }), '  The label is on the back.  ')
    await user.upload(screen.getByLabelText('Invoice or receipt'), file('invoice-clear.pdf', 'application/pdf'))
    await user.upload(screen.getByLabelText(/Photos of the product/), [file('label.jpg', 'image/jpeg')])
    await user.click(screen.getByRole('button', { name: 'Send information' }))

    expect(await screen.findByText('Thank you — we received your information')).toBeInTheDocument()
    expect(supplements).toEqual([
      { authorization: 'Bearer claimant-token', note: 'The label is on the back.', invoice: ['invoice-clear.pdf'], photos: ['label.jpg'] },
    ])
    expect(screen.queryByRole('form', { name: 'Send information' })).not.toBeInTheDocument()
    expect(screen.getByText("We're checking your claim")).toBeInTheDocument()
    expect(stepState('Evaluation')).toBe('current step')
    // Polling resumed for the new round.
    const requests = statusRequests.length
    await wait(80)
    expect(statusRequests.length).toBeGreaterThan(requests)
  })

  it('explains when the claim is no longer waiting for information', async () => {
    respondWith(pendingInformation)
    onSupplement(() =>
      HttpResponse.json(
        { title: 'Conflict', status: 409, detail: 'The claim is not pending information.', correlationId: 'corr-409' },
        { status: 409 },
      ),
    )
    const user = userEvent.setup({ applyAccept: false })
    renderStatus()

    await user.type(await screen.findByRole('textbox', { name: /Anything else/ }), 'Bought on 15 January.')
    await user.click(screen.getByRole('button', { name: 'Send information' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('This claim is no longer waiting for information')
    expect(alert).toHaveTextContent('corr-409')
  })

  it('attaches the API field errors to the supplement fields', async () => {
    respondWith(pendingInformation)
    onSupplement(() =>
      HttpResponse.json(
        { title: 'Validation failed', status: 400, errors: { invoice: ['The invoice is not a readable PDF.'] } },
        { status: 400 },
      ),
    )
    const user = userEvent.setup({ applyAccept: false })
    renderStatus()

    await user.upload(await screen.findByLabelText('Invoice or receipt'), file('invoice.pdf', 'application/pdf'))
    await user.click(screen.getByRole('button', { name: 'Send information' }))

    expect(await screen.findByText('The invoice is not a readable PDF.')).toBeInTheDocument()
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
