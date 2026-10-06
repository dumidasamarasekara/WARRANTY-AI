import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ClaimForm, type ClaimFormVariant } from '../../../src/features/claimant/ClaimForm'
import type { SubmissionAccepted } from '../../../src/features/claimant/claimSubmission'
import { file, fillDetails, fillEvidence, textbox, validDetails } from '../../support/claimForm'
import { useNodeMultipartClasses } from '../../support/multipart'

const accepted: SubmissionAccepted = { reference: 'K7M2Q9X4TB', status: 'Submitted', round: 1 }

beforeEach(async () => {
  await useNodeMultipartClasses()
})
afterEach(() => vi.unstubAllGlobals())

function renderForm(variant: ClaimFormVariant = 'claimant') {
  const submit = vi.fn((body: FormData) => {
    void body
    return Promise.resolve(accepted)
  })
  const onSubmitted = vi.fn()
  const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } })
  render(
    <QueryClientProvider client={client}>
      <ClaimForm variant={variant} submit={submit} onSubmitted={onSubmitted} />
    </QueryClientProvider>,
  )
  return { user: userEvent.setup({ applyAccept: false }), submit, onSubmitted }
}

const continueButton = () => screen.getByRole('button', { name: 'Continue' })
const submitButton = () => screen.getByRole('button', { name: 'Submit claim' })

const requiredMessages = [
  'Enter the full name',
  'Enter an email address',
  'Enter the country',
  'Enter the model code',
  'Enter the serial number',
  'Enter the purchase date',
  'Enter where the product was bought',
  'Enter the price paid',
  'Enter the currency',
  'Enter the country of purchase',
]

// Filling the whole form through user-event takes a few seconds on a loaded machine.
describe('ClaimForm validation', { timeout: 20_000 }, () => {
  it.each<ClaimFormVariant>(['claimant', 'staff'])(
    'flags every required detail in the %s form and leaves the optional fields alone',
    async (variant) => {
      const { user, submit } = renderForm(variant)

      await user.click(continueButton())

      for (const message of requiredMessages) expect(screen.getByText(message)).toBeInTheDocument()
      for (const optional of ['Phone number', 'Address', 'City', 'Postal code']) {
        expect(textbox(optional)).not.toHaveAttribute('aria-invalid', 'true')
      }
      expect(textbox('Full name')).toHaveFocus()
      expect(screen.getByRole('button', { name: 'Continue' })).toBeInTheDocument()
      expect(submit).not.toHaveBeenCalled()
    },
  )

  it('clears a field error as soon as the field is edited', async () => {
    const { user } = renderForm()
    await user.click(continueButton())

    await user.type(textbox('Full name'), 'A')

    expect(screen.queryByText('Enter the full name')).not.toBeInTheDocument()
    expect(textbox('Full name')).not.toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByText('Enter an email address')).toBeInTheDocument()
  })

  it('upper-cases country and currency codes as typed and checks their length', async () => {
    const { user } = renderForm()
    await fillDetails(user, { Country: 'u', Currency: 'us', 'Country of purchase': 'usa' })

    expect(textbox('Country')).toHaveValue('U')
    expect(textbox('Currency')).toHaveValue('US')
    // maxLength stops the third letter of a country code.
    expect(textbox('Country of purchase')).toHaveValue('US')

    await user.click(continueButton())

    expect(screen.getByText('Use the two-letter country code, e.g. US')).toBeInTheDocument()
    expect(screen.getByText('Use the three-letter currency code, e.g. USD')).toBeInTheDocument()
    expect(textbox('Country')).toHaveFocus()
  })

  it('refuses a zero price and an over-long name', async () => {
    const { user } = renderForm()
    await fillDetails(user, { 'Price paid': '0', 'Full name': '' })
    await user.click(textbox('Full name'))
    await user.paste('x'.repeat(201))

    await user.click(continueButton())

    expect(screen.getByText('Enter the price as a number greater than 0, e.g. 499.99')).toBeInTheDocument()
    expect(screen.getByText('Use at most 200 characters')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Submit claim' })).not.toBeInTheDocument()
  })

  it('validates the evidence step before posting and keeps the details when going back', async () => {
    const { user, submit } = renderForm()
    await fillDetails(user)
    await user.click(continueButton())

    await user.click(submitButton())
    expect(screen.getByText('Describe what went wrong')).toBeInTheDocument()
    expect(screen.getByText('Add the invoice')).toBeInTheDocument()
    expect(screen.getByText('Add at least one photo of the product')).toBeInTheDocument()

    await user.upload(screen.getByLabelText('Invoice'), file('notes.txt', 'text/plain'))
    expect(screen.getByText("notes.txt can't be used: send a PDF, JPG, PNG or WebP file")).toBeInTheDocument()
    await user.upload(screen.getByLabelText(/Photos of the product/), file('empty.jpg', 'image/jpeg', 0))
    expect(screen.getByText('empty.jpg is empty')).toBeInTheDocument()
    expect(submit).not.toHaveBeenCalled()

    await user.click(screen.getByRole('button', { name: 'Back' }))
    expect(textbox('Full name')).toHaveValue(validDetails['Full name'])
    expect(textbox('Model code')).toHaveValue(validDetails['Model code'])
  })

  it('posts once everything is valid and hands back the reference and contact', async () => {
    const { user, submit, onSubmitted } = renderForm('staff')
    await fillDetails(user)
    await user.click(continueButton())
    await fillEvidence(user)

    await user.click(submitButton())

    await waitFor(() => expect(onSubmitted).toHaveBeenCalledWith(accepted, { email: 'alex@example.test', phone: '+1 555 0100' }))
    expect(submit).toHaveBeenCalledTimes(1)
    const body = submit.mock.calls[0]![0]
    const claim = body.get('claim')
    expect(claim).toBeInstanceOf(Blob)
    expect(JSON.parse(await (claim as Blob).text())).toMatchObject({
      customer: { country: 'US' },
      purchase: { price: 499.99, currency: 'USD', country: 'US' },
    })
    expect(body.getAll('photos')).toHaveLength(2)
  })
})
