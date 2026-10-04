import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { RouterProvider, createMemoryRouter, useParams } from 'react-router'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { NewClaimPage } from '../../../src/features/claims/NewClaimPage'
import { setStaffAccessTokenProvider } from '../../../src/shared/api/client'
import { fillDetails, fillEvidence, textbox } from '../../support/claimForm'
import { useNodeMultipartClasses } from '../../support/multipart'

interface Received {
  authorization: string | null
  claim: unknown
  invoice: number
  photos: number
}

let received: Received[] = []
const server = setupServer()

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
afterAll(() => server.close())

beforeEach(async () => {
  received = []
  setStaffAccessTokenProvider(() => 'staff-token')
  await useNodeMultipartClasses()
})

afterEach(() => {
  server.resetHandlers()
  vi.unstubAllGlobals()
  setStaffAccessTokenProvider(() => undefined)
})

function onSubmit(response: () => Response) {
  server.use(
    http.post('*/api/claims', async ({ request }) => {
      const form = await request.formData()
      const claim = form.get('claim')
      received.push({
        authorization: request.headers.get('Authorization'),
        claim: claim instanceof Blob ? JSON.parse(await claim.text()) : claim,
        invoice: form.getAll('invoice').length,
        photos: form.getAll('photos').length,
      })
      return response()
    }),
  )
}

function ClaimPage() {
  return <h1>Claim {useParams().claimId}</h1>
}

function renderPage() {
  const router = createMemoryRouter(
    [
      { path: '/staff/claims/new', element: <NewClaimPage /> },
      { path: '/staff/claims/:claimId', element: <ClaimPage /> },
    ],
    { initialEntries: ['/staff/claims/new'] },
  )
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return userEvent.setup({ applyAccept: false })
}

async function submitValidClaim(user: ReturnType<typeof renderPage>) {
  await fillDetails(user)
  await user.click(screen.getByRole('button', { name: 'Continue' }))
  await fillEvidence(user)
  await user.click(screen.getByRole('button', { name: 'Submit claim' }))
}

describe('NewClaimPage', () => {
  it('shows the staff form with agent copy and a two-step stepper', () => {
    renderPage()
    expect(screen.getByRole('heading', { level: 1, name: 'New claim' })).toBeInTheDocument()
    expect(screen.getByText('Submitted on behalf of a customer · Agent portal')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Customer and product' })).toBeInTheDocument()
    const stepper = screen.getByRole('list', { name: 'Claim progress' })
    expect(within(stepper).getAllByRole('listitem')).toHaveLength(2)
  })

  it('validates like the claimant form before posting', async () => {
    const user = renderPage()
    await user.click(screen.getByRole('button', { name: 'Continue' }))
    expect(screen.getByText('Enter the full name')).toBeInTheDocument()
    expect(textbox('Full name')).toHaveFocus()
    expect(received).toHaveLength(0)
  })

  it('posts the multipart claim to POST /api/claims with the staff token and links to the new claim', async () => {
    onSubmit(() =>
      HttpResponse.json(
        { claimId: '6f1c2a9e-3b7d-4c55-9a1e-2d8f0b7c4e11', reference: 'K7M2Q9X4TB', status: 'Submitted', round: 1 },
        { status: 202 },
      ),
    )
    const user = renderPage()
    await submitValidClaim(user)

    // The heading is focused by a passive effect that can run after findByRole sees it in the DOM.
    const heading = await screen.findByRole('heading', { name: 'Claim submitted' })
    await waitFor(() => expect(heading).toHaveFocus())
    expect(received).toEqual([
      {
        authorization: 'Bearer staff-token',
        claim: expect.objectContaining({ customer: expect.objectContaining({ email: 'alex@example.test', country: 'US' }) }),
        invoice: 1,
        photos: 2,
      },
    ])
    expect(screen.getByText('K7M2Q9X4TB')).toBeInTheDocument()

    await user.click(screen.getByRole('link', { name: 'Open claim' }))
    expect(await screen.findByRole('heading', { name: 'Claim 6f1c2a9e-3b7d-4c55-9a1e-2d8f0b7c4e11' })).toBeInTheDocument()
  })

  it('starts an empty form for another claim', async () => {
    onSubmit(() => HttpResponse.json({ claimId: 'c-1', reference: 'K7M2Q9X4TB', status: 'Submitted', round: 1 }, { status: 202 }))
    const user = renderPage()
    await submitValidClaim(user)
    await user.click(await screen.findByRole('button', { name: 'Submit another claim' }))

    expect(screen.getByRole('heading', { name: 'Customer and product' })).toBeInTheDocument()
    expect(textbox('Full name')).toHaveValue('')
  })

  it('shows a refused submission with its correlation ID', async () => {
    onSubmit(() =>
      HttpResponse.json(
        { title: 'Forbidden', status: 403, detail: 'Only claims agents can submit claims.', correlationId: 'abc123' },
        { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    )
    const user = renderPage()
    await submitValidClaim(user)

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Forbidden')
    expect(alert).toHaveTextContent('Only claims agents can submit claims.')
    expect(alert).toHaveTextContent('abc123')
  })
})
