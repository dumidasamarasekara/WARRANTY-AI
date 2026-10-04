import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { RouterProvider, createMemoryRouter, useParams } from 'react-router'
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest'
import { AccessClaimPage } from '../../../src/features/claimant/AccessClaimPage'
import { claimantTokenFor, clearClaimantToken } from '../../../src/shared/api/claimantToken'
import { api } from '../../../src/shared/api/client'

const server = setupServer()
let accessBodies: unknown[] = []

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
afterEach(() => {
  server.resetHandlers()
  accessBodies = []
  clearClaimantToken()
  localStorage.clear()
  sessionStorage.clear()
})
afterAll(() => server.close())

const inOneHour = () => new Date(Date.now() + 3_600_000).toISOString()

function StatusProbe() {
  return <h1>Status of {useParams().reference}</h1>
}

function renderAccess(path = '/claims/access') {
  const router = createMemoryRouter(
    [
      { path: '/claims/access', element: <AccessClaimPage /> },
      { path: '/claims/:reference', element: <StatusProbe /> },
    ],
    { initialEntries: [path] },
  )
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
}

function respondToAccess(response: () => Response) {
  server.use(
    http.post('*/api/public/claims/access', async ({ request }) => {
      accessBodies.push(await request.json())
      return response()
    }),
  )
}

async function fillAndSubmit(reference: string, contact: string) {
  await userEvent.type(screen.getByLabelText(/Claim reference/), reference)
  await userEvent.type(screen.getByLabelText(/Email address or phone number/), contact)
  await userEvent.click(screen.getByRole('button', { name: 'View claim' }))
}

describe('AccessClaimPage', () => {
  it('exchanges the reference and contact for a token held in memory only, then opens the status page', async () => {
    respondToAccess(() => HttpResponse.json({ accessToken: 'claimant-token', expiresAt: inOneHour() }))
    renderAccess()

    await fillAndSubmit(' abcd234567 ', 'someone@example.test')

    expect(await screen.findByRole('heading', { name: 'Status of ABCD234567' })).toBeInTheDocument()
    expect(accessBodies).toEqual([{ reference: 'ABCD234567', contact: 'someone@example.test' }])
    expect(claimantTokenFor('ABCD234567')).toBe('claimant-token')
    expect(localStorage.length).toBe(0)
    expect(sessionStorage.length).toBe(0)

    // The held token goes on the claim-scoped call through the shared client.
    let authorization: string | null = null
    server.use(
      http.get('*/api/public/claims/:reference', ({ request }) => {
        authorization = request.headers.get('Authorization')
        return HttpResponse.json({ reference: 'ABCD234567', status: 'Submitted', submittedAt: inOneHour() })
      }),
    )
    await api.GET('/api/public/claims/{reference}', { params: { path: { reference: 'ABCD234567' } } })
    expect(authorization).toBe('Bearer claimant-token')
  })

  it('shows the same generic error for any 401 and holds no token', async () => {
    respondToAccess(() =>
      HttpResponse.json(
        { title: 'Unauthorized', status: 401, detail: 'Reference not found' },
        { status: 401, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    )
    renderAccess()

    await fillAndSubmit('ABCD234567', '+15550100')

    expect(await screen.findByRole('alert')).toHaveTextContent("We couldn't find a claim with those details")
    expect(screen.queryByText('Reference not found')).not.toBeInTheDocument()
    expect(claimantTokenFor('ABCD234567')).toBeUndefined()
    expect(screen.getByRole('heading', { name: 'Check your claim' })).toBeInTheDocument()
  })

  it('tells the claimant when to try again after too many attempts', async () => {
    respondToAccess(
      () =>
        new HttpResponse(JSON.stringify({ title: 'Too Many Requests', status: 429 }), {
          status: 429,
          headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '600' },
        }),
    )
    renderAccess()

    await fillAndSubmit('ABCD234567', 'someone@example.test')

    expect(await screen.findByRole('alert')).toHaveTextContent('Too many attempts — try again in 10 minutes')
  })

  it('asks for both fields before calling the API', async () => {
    respondToAccess(() => HttpResponse.json({ accessToken: 'claimant-token', expiresAt: inOneHour() }))
    renderAccess()

    await userEvent.click(screen.getByRole('button', { name: 'View claim' }))

    expect(screen.getByText('Enter your claim reference')).toBeInTheDocument()
    expect(screen.getByText('Enter the email address or phone number you gave with your claim')).toBeInTheDocument()
    expect(accessBodies).toEqual([])
  })

  it('prefills the reference from the address', () => {
    renderAccess('/claims/access?reference=ABCD234567')
    expect(screen.getByLabelText(/Claim reference/)).toHaveValue('ABCD234567')
  })
})
