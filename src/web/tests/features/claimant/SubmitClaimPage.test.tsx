import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { RouterProvider, createMemoryRouter, useLocation } from 'react-router'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { SubmitClaimPage } from '../../../src/features/claimant/SubmitClaimPage'
import { localToday } from '../../../src/features/claimant/claimSubmission'
import { file, fillDetails, fillEvidence, textbox } from '../../support/claimForm'
import { useNodeMultipartClasses } from '../../support/multipart'

interface Received {
  claim: unknown
  claimType: string
  invoice: string[]
  photos: string[]
}

let received: Received[] = []

const server = setupServer()

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
afterAll(() => server.close())

beforeEach(async () => {
  received = []
  await useNodeMultipartClasses()
})

afterEach(() => {
  server.resetHandlers()
  vi.unstubAllGlobals()
})

const names = (parts: FormDataEntryValue[]) => parts.map((part) => (typeof part === 'string' ? part : part.name))

/** Records the multipart parts of each submission, then answers with `response`. */
function onSubmit(response: () => Response) {
  server.use(
    http.post('*/api/public/claims', async ({ request }) => {
      const form = await request.formData()
      const claim = form.get('claim')
      received.push({
        claim: claim instanceof Blob ? JSON.parse(await claim.text()) : claim,
        claimType: claim instanceof Blob ? claim.type : typeof claim,
        invoice: names(form.getAll('invoice')),
        photos: names(form.getAll('photos')),
      })
      return response()
    }),
  )
}

function AccessPage() {
  const location = useLocation()
  return <h1>Access page {location.search}</h1>
}

function renderPage() {
  const router = createMemoryRouter(
    [
      { path: '/', element: <SubmitClaimPage /> },
      { path: '/claims/access', element: <AccessPage /> },
    ],
    { initialEntries: ['/'] },
  )
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return userEvent.setup({ applyAccept: false })
}

const continueButton = () => screen.getByRole('button', { name: 'Continue' })
const submitButton = () => screen.getByRole('button', { name: 'Submit claim' })

describe('SubmitClaimPage', () => {
  it('shows the claimant stepper at step 1 and requires the details before continuing', async () => {
    const user = renderPage()
    const stepper = screen.getByRole('list', { name: 'Claim progress' })
    expect(within(stepper).getByText('Details').closest('li')).toHaveAttribute('aria-current', 'step')

    await user.click(continueButton())

    expect(screen.getByText('Enter the full name')).toBeInTheDocument()
    expect(screen.getByText('Enter an email address')).toBeInTheDocument()
    expect(screen.getByText('Enter the purchase date')).toBeInTheDocument()
    expect(textbox('Full name')).toHaveAttribute('aria-invalid', 'true')
    expect(textbox('Full name')).toHaveFocus()
    expect(screen.getByRole('heading', { name: 'Your details' })).toBeInTheDocument()
    expect(received).toHaveLength(0)
  })

  it('rejects a purchase date in the future, an invalid email and a non-numeric price', async () => {
    const user = renderPage()
    await fillDetails(user, { 'Email address': 'not-an-email', 'Price paid': 'abc' })
    const future = localToday(new Date(Date.now() + 2 * 86_400_000))
    fireEvent.change(screen.getByLabelText(/Purchase date/), { target: { value: future } })

    await user.click(continueButton())

    expect(screen.getByText("The purchase date can't be in the future or after the claim date")).toBeInTheDocument()
    expect(screen.getByText('Enter an email address like name@example.com')).toBeInTheDocument()
    expect(screen.getByText('Enter the price as a number greater than 0, e.g. 499.99')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'What went wrong and your files' })).not.toBeInTheDocument()
  })

  it('requires a description, an invoice and at least one photo, and refuses HEIC photos', async () => {
    const user = renderPage()
    await fillDetails(user)
    await user.click(continueButton())
    expect(screen.getByRole('heading', { name: 'What went wrong and your files' })).toHaveFocus()

    await user.type(textbox('What went wrong?'), 'Too short')
    await user.click(submitButton())

    expect(screen.getByText('Describe the problem in at least 20 characters')).toBeInTheDocument()
    expect(screen.getByText('Add the invoice')).toBeInTheDocument()
    expect(screen.getByText('Add at least one photo of the product')).toBeInTheDocument()

    await user.upload(screen.getByLabelText(/Photos of the product/), file('IMG_0001.HEIC', 'image/heic'))
    expect(screen.getByText('Please send photos as JPEG or PNG')).toBeInTheDocument()
    expect(screen.queryByText('IMG_0001.HEIC')).not.toBeInTheDocument()

    await user.upload(screen.getByLabelText('Invoice'), file('invoice.pdf', 'application/pdf', 16 * 1024 * 1024))
    expect(screen.getByText('invoice.pdf is larger than 15 MB')).toBeInTheDocument()
    expect(received).toHaveLength(0)
  })

  it('accepts at most 8 photos', async () => {
    const user = renderPage()
    await fillDetails(user)
    await user.click(continueButton())

    const photos = Array.from({ length: 9 }, (_, index) => file(`photo-${index + 1}.jpg`, 'image/jpeg'))
    await user.upload(screen.getByLabelText(/Photos of the product/), photos)

    expect(screen.getByText('You can add up to 8 photos')).toBeInTheDocument()
    expect(screen.getByText('photo-8.jpg')).toBeInTheDocument()
    expect(screen.queryByText('photo-9.jpg')).not.toBeInTheDocument()
  })

  it('posts the claim as multipart parts and shows the reference and the contact for access', async () => {
    onSubmit(() => HttpResponse.json({ reference: 'K7M2Q9X4TB', status: 'Submitted', round: 1 }, { status: 202 }))
    const user = renderPage()
    await fillDetails(user)
    await user.click(continueButton())
    await fillEvidence(user)
    await user.click(submitButton())

    expect(await screen.findByRole('heading', { name: 'Claim submitted' })).toHaveFocus()
    expect(received).toEqual([
      {
        claim: {
          customer: { fullName: 'Alex Example', email: 'alex@example.test', phone: '+1 555 0100', country: 'US' },
          product: { modelCode: 'AP7-220', serialNumber: 'SN-0001' },
          purchase: { date: '2025-01-15', place: 'Example Store', price: 499.99, currency: 'USD', country: 'US' },
          problemDescription: 'The control panel stopped responding after two weeks of normal use.',
        },
        claimType: 'application/json',
        invoice: ['invoice.pdf'],
        photos: ['front.jpg', 'label.png'],
      },
    ])
    expect(screen.getByText('K7M2Q9X4TB')).toBeInTheDocument()
    expect(screen.getByText('alex@example.test')).toBeInTheDocument()
    expect(screen.getByText('+1 555 0100')).toBeInTheDocument()
    const stepper = screen.getByRole('list', { name: 'Claim progress' })
    expect(within(stepper).getByText('Evaluation').closest('li')).toHaveAttribute('aria-current', 'step')

    await user.click(screen.getByRole('button', { name: 'Check status' }))
    expect(await screen.findByRole('heading', { name: 'Access page ?reference=K7M2Q9X4TB' })).toBeInTheDocument()
  })

  it('attaches API field errors to their fields and returns to the step that has them', async () => {
    onSubmit(() =>
      HttpResponse.json(
        {
          title: 'One or more validation errors occurred.',
          status: 400,
          correlationId: '4bf92f3577b34da6a3ce929d0e0e4736',
          errors: { 'purchase.date': ['The purchase date is after the claim date.'], claim: ['Unexpected field "tenantId".'] },
        },
        { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    )
    const user = renderPage()
    await fillDetails(user)
    await user.click(continueButton())
    await fillEvidence(user)
    await user.click(submitButton())

    expect(await screen.findByText('The purchase date is after the claim date.')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Your details' })).toBeInTheDocument()
    expect(screen.getByLabelText(/Purchase date/)).toHaveAttribute('aria-invalid', 'true')
    const alert = screen.getByRole('alert')
    expect(alert).toHaveTextContent('One or more validation errors occurred.')
    expect(alert).toHaveTextContent('Unexpected field "tenantId".')
    expect(alert).toHaveTextContent('4bf92f3577b34da6a3ce929d0e0e4736')
  })

  it('shows the ProblemDetails of a rejected upload with its correlation ID', async () => {
    onSubmit(() =>
      HttpResponse.json(
        {
          title: 'Unsupported file type',
          status: 415,
          detail: 'Please send photos as JPEG or PNG.',
          correlationId: '0af7651916cd43dd8448eb211c80319c',
        },
        { status: 415, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    )
    const user = renderPage()
    await fillDetails(user)
    await user.click(continueButton())
    await fillEvidence(user)
    await user.click(submitButton())

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Unsupported file type')
    expect(alert).toHaveTextContent('Please send photos as JPEG or PNG.')
    expect(alert).toHaveTextContent('0af7651916cd43dd8448eb211c80319c')
    expect(screen.getByRole('heading', { name: 'What went wrong and your files' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Claim submitted' })).not.toBeInTheDocument()
  })
})
