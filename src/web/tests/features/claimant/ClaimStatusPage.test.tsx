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

type RepairStage = 'AwaitingHandover' | 'AtRepairCentre' | 'ReadyForCollection'
const repairLabels: Record<RepairStage, [string, number]> = {
  AwaitingHandover: ['Take your product to the repair centre', 0],
  AtRepairCentre: ['With repair centre', 1],
  ReadyForCollection: ['Ready for collection', 2],
}

/** `ClaimantRemedyView` for a Repair, as the API projects it (data-model.md "Claimant projection"). */
function repairRemedy(stage: RepairStage) {
  const [label, current] = repairLabels[stage]
  return {
    remedy: 'Repair',
    label,
    completed: false,
    repairCentre: {
      name: 'North Service Hub',
      address: '12 Harbour Road, Springfield',
      openingHours: 'Mon–Fri 08:00–17:00',
    },
    steps: ['Handed over', 'With repair centre', 'Ready for collection', 'Collected'].map((title, i) => ({
      title,
      state: i < current ? 'done' : i === current ? 'current' : 'upcoming',
    })),
  }
}
type ReplacementStage = 'AwaitingReturn' | 'AwaitingUnit' | 'ReadyForCollection' | 'HandedToCustomer'
const replacementLabels: Record<ReplacementStage, [string, number]> = {
  AwaitingReturn: ['Please bring your product to the location below', 0],
  AwaitingUnit: ['We are preparing your replacement', 1],
  ReadyForCollection: ['Your replacement is ready for collection', 1],
  HandedToCustomer: ['Replacement handed over', 3],
}

/**
 * `ClaimantRemedyView` for a Replacement, as the API projects it (003 data-model.md "Claimant projection"): "Product
 * returned" only when the faulty product had to come back first.
 */
function replacementRemedy(stage: ReplacementStage, returnFirst = false) {
  const [label, current] = replacementLabels[stage]
  const ready = stage === 'ReadyForCollection' || stage === 'HandedToCustomer'
  return {
    remedy: 'Replacement',
    label,
    completed: stage === 'HandedToCustomer',
    steps: [],
    location: { name: 'North Service Point', address: '1 Service Street, Springfield', openingHours: 'Mon–Fri 09:00–17:00' },
    ...(ready ? { replacementModelName: 'Aurora Tab 10' } : {}),
    replacementSteps: ['Product returned', 'Ready for collection', 'Handed to you']
      .map((title, i) => ({ title, state: i < current ? 'done' : i === current ? 'current' : 'upcoming' }))
      .slice(returnFirst ? 0 : 1),
  }
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

  it('completes the stepper for an approved claim without the old service centre step', async () => {
    respondWith({ status: 'Approved', outcomeExplanation: 'Covered.', remedy: repairRemedy('AtRepairCentre') })
    renderStatus()

    await screen.findByText('Your claim is approved')
    expect(stepState('Evaluation')).toBe('completed')
    expect(stepState('Decision')).toBe('completed')
    const timeline = screen.getByRole('list', { name: 'What happens next' })
    expect(within(timeline).getByText('Received')).toBeInTheDocument()
    expect(document.body).not.toHaveTextContent(/service centre will contact you/i)
  })

  it('shows the remedy line, the four repair steps and the repair centre', async () => {
    respondWith({ status: 'Approved', outcomeExplanation: 'Covered.', remedy: repairRemedy('AtRepairCentre') })
    renderStatus()

    const remedy = await screen.findByRole('region', { name: 'Remedy' })
    expect(remedy).toHaveTextContent('Remedy: Repair')
    expect(remedy).toHaveTextContent('With repair centre')
    const steps = within(within(remedy).getByRole('list', { name: 'Repair progress' })).getAllByRole('listitem')
    expect(steps.map((step) => step.textContent)).toEqual([
      'Handed over Done',
      'With repair centre In progress',
      'Ready for collection Next',
      'Collected Next',
    ])
    expect(remedy).toHaveTextContent('North Service Hub')
    expect(remedy).toHaveTextContent('12 Harbour Road, Springfield')
    expect(remedy).toHaveTextContent('Opening hours: Mon–Fri 08:00–17:00')
    expect(within(remedy).queryByText('Your product is ready for collection')).not.toBeInTheDocument()
  })

  it('asks the claimant to take the product to the repair centre before the hand-over', async () => {
    respondWith({ status: 'Approved', remedy: repairRemedy('AwaitingHandover') })
    renderStatus()

    const remedy = await screen.findByRole('region', { name: 'Remedy' })
    expect(remedy).toHaveTextContent('Take your product to the repair centre')
    expect(within(remedy).getByText('Handed over')).toBeInTheDocument()
    expect(remedy).toHaveTextContent('Handed over In progress')
  })

  it('shows the new repair centre after a transfer', async () => {
    respondWith({
      status: 'Approved',
      remedy: {
        ...repairRemedy('AwaitingHandover'),
        repairCentre: { name: 'South Service Hub', address: '4 Quay Street, Springfield', openingHours: 'Mon–Sat 09:00–18:00' },
      },
    })
    renderStatus()

    const remedy = await screen.findByRole('region', { name: 'Remedy' })
    const centre = within(remedy).getByRole('heading', { name: 'Repair centre' }).closest('section, div')!.parentElement!
    expect(centre).toHaveTextContent('South Service Hub')
    expect(centre).toHaveTextContent('4 Quay Street, Springfield')
    expect(centre).toHaveTextContent('Opening hours: Mon–Sat 09:00–18:00')
    expect(remedy).toHaveTextContent('Take your product to the repair centre')
    expect(document.body).not.toHaveTextContent('North Service Hub')
  })

  it('turns the centre card into a ready-for-collection callout', async () => {
    respondWith({ status: 'Approved', remedy: repairRemedy('ReadyForCollection') })
    renderStatus()

    const remedy = await screen.findByRole('region', { name: 'Remedy' })
    const callout = within(remedy).getByRole('status')
    expect(callout).toHaveTextContent('Your product is ready for collection')
    expect(callout).toHaveTextContent('North Service Hub')
    expect(callout).toHaveTextContent('12 Harbour Road, Springfield')
    expect(callout).toHaveTextContent('Opening hours: Mon–Fri 08:00–17:00')
  })

  it.each([
    ['We are deciding how to resolve your claim'],
    ['Repair not possible. Your claim is being reviewed.'],
  ])('shows a neutral banner with the label only while a remedy is decided: %s', async (label) => {
    respondWith({ status: 'Approved', remedy: { label, completed: false, steps: [] } })
    renderStatus()

    const remedy = await screen.findByRole('region', { name: 'Remedy' })
    expect(remedy).toHaveTextContent(label)
    expect(remedy.textContent).toBe(label)
    expect(screen.queryByRole('list', { name: 'Repair progress' })).not.toBeInTheDocument()
  })

  it.each([
    ['Replacement', false, 'We are preparing your replacement'],
    ['Replacement', true, 'Replacement handed over'],
    ['Refund', false, 'Refund being processed'],
    ['Refund', true, 'Refund completed'],
  ])('shows the %s label (completed: %s) without repair steps or a centre', async (kind, completed, label) => {
    respondWith({ status: 'Approved', remedy: { remedy: kind, label, completed, steps: [] } })
    renderStatus()

    const remedy = await screen.findByRole('region', { name: 'Remedy' })
    expect(remedy).toHaveTextContent(`Remedy: ${kind}`)
    expect(within(remedy).getByText(label)).toBeInTheDocument()
    expect(within(remedy).queryByRole('list', { name: 'Repair progress' })).not.toBeInTheDocument()
    expect(within(remedy).queryByRole('heading', { name: 'Repair centre' })).not.toBeInTheDocument()
    const callout = within(remedy).queryByRole('status')
    if (completed) expect(callout).toHaveTextContent(label)
    else expect(callout).not.toBeInTheDocument()
  })

  it('shows a replacement being prepared with its steps and the location, without "on its way"', async () => {
    respondWith({ status: 'Approved', remedy: replacementRemedy('AwaitingUnit') })
    renderStatus()

    const remedy = await screen.findByRole('region', { name: 'Remedy' })
    expect(remedy).toHaveTextContent('Remedy: Replacement')
    expect(remedy).toHaveTextContent('We are preparing your replacement')
    const steps = within(within(remedy).getByRole('list', { name: 'Replacement progress' })).getAllByRole('listitem')
    expect(steps.map((step) => step.textContent)).toEqual(['Ready for collection In progress', 'Handed to you Next'])
    const location = within(remedy).getByRole('heading', { name: 'Where to collect your replacement' }).closest('section, div')!.parentElement!
    expect(location).toHaveTextContent('North Service Point')
    expect(location).toHaveTextContent('1 Service Street, Springfield')
    expect(location).toHaveTextContent('Opening hours: Mon–Fri 09:00–17:00')
    expect(within(remedy).queryByRole('status')).not.toBeInTheDocument()
    expect(document.body).not.toHaveTextContent(/shipped|on its way|delivered/i)
  })

  it('asks the claimant to bring the product first when the policy wants it before reservation', async () => {
    respondWith({ status: 'Approved', remedy: replacementRemedy('AwaitingReturn', true) })
    renderStatus()

    const remedy = await screen.findByRole('region', { name: 'Remedy' })
    expect(remedy).toHaveTextContent('Please bring your product to the location below')
    const steps = within(within(remedy).getByRole('list', { name: 'Replacement progress' })).getAllByRole('listitem')
    expect(steps.map((step) => step.textContent)).toEqual(['Product returned In progress', 'Ready for collection Next', 'Handed to you Next'])
    const card = within(remedy).getByRole('heading', { name: 'Please bring your product to' }).closest('section, div')!.parentElement!
    expect(card).toHaveTextContent('North Service Point')
  })

  it('turns the location into a ready callout with the model and asks for the faulty product under AtCollection', async () => {
    respondWith({ status: 'Approved', remedy: replacementRemedy('ReadyForCollection') })
    renderStatus()

    const remedy = await screen.findByRole('region', { name: 'Remedy' })
    const callout = within(remedy).getByRole('status')
    expect(callout).toHaveTextContent('Your replacement is ready for collection')
    expect(callout).toHaveTextContent('Aurora Tab 10')
    expect(callout).toHaveTextContent('North Service Point')
    expect(callout).toHaveTextContent('Opening hours: Mon–Fri 09:00–17:00')
    expect(callout).toHaveTextContent('Please bring your faulty product with you.')
    expect(within(remedy).getAllByText('Your replacement is ready for collection')).toHaveLength(1)
  })

  it('does not ask for the faulty product again when it was returned before reservation', async () => {
    respondWith({ status: 'Approved', remedy: replacementRemedy('ReadyForCollection', true) })
    renderStatus()

    const callout = within(await screen.findByRole('region', { name: 'Remedy' })).getByRole('status')
    expect(callout).toHaveTextContent('Your replacement is ready for collection')
    expect(callout).not.toHaveTextContent('Please bring your faulty product')
  })

  it('shows the location card "Please bring your product to" without contact or return check results', async () => {
    respondWith({
      status: 'Approved',
      remedy: {
        ...replacementRemedy('AwaitingReturn', true),
        returnState: 'Mismatch',
        serialMatches: false,
        serialSeen: 'AT10-7777',
        contact: '[LOCATION CONTACT]',
      },
    })
    renderStatus()

    const remedy = await screen.findByRole('region', { name: 'Remedy' })
    const card = within(remedy).getByRole('heading', { name: 'Please bring your product to' }).closest('section, div')!.parentElement!
    expect(card).toHaveTextContent('North Service Point')
    expect(card).toHaveTextContent('1 Service Street, Springfield')
    expect(card).toHaveTextContent('Opening hours: Mon–Fri 09:00–17:00')
    expect(document.body.textContent).not.toMatch(/AT10-7777|\[LOCATION CONTACT\]|does not match|mismatch/i)
  })

  it('shows a product held by a repair centre as returned and asks for nothing more', async () => {
    // The API adds "Product returned" (done) when the product stayed at the repair centre, also under AtCollection.
    respondWith({ status: 'Approved', remedy: replacementRemedy('AwaitingUnit', true) })
    renderStatus()

    const remedy = await screen.findByRole('region', { name: 'Remedy' })
    const steps = within(within(remedy).getByRole('list', { name: 'Replacement progress' })).getAllByRole('listitem')
    expect(steps.map((step) => step.textContent)).toEqual(['Product returned Done', 'Ready for collection In progress', 'Handed to you Next'])
    expect(within(remedy).queryByRole('heading', { name: 'Please bring your product to' })).not.toBeInTheDocument()
    expect(within(remedy).getByRole('heading', { name: 'Where to collect your replacement' })).toBeInTheDocument()
    expect(remedy).not.toHaveTextContent(/repair centre/i)
  })

  it('shows a handed-over replacement as done, without the location card', async () => {
    respondWith({ status: 'Approved', remedy: replacementRemedy('HandedToCustomer') })
    renderStatus()

    const remedy = await screen.findByRole('region', { name: 'Remedy' })
    expect(within(remedy).getByRole('status')).toHaveTextContent('Replacement handed over')
    const steps = within(within(remedy).getByRole('list', { name: 'Replacement progress' })).getAllByRole('listitem')
    expect(steps.map((step) => step.textContent)).toEqual(['Ready for collection Done', 'Handed to you Done'])
    expect(remedy).not.toHaveTextContent('North Service Point')
  })

  it('says "Replacement not possible" in a pending banner with the label only', async () => {
    const label = 'Replacement not possible. Your claim is being reviewed.'
    respondWith({ status: 'Approved', remedy: { label, completed: false, steps: [] } })
    renderStatus()

    const remedy = await screen.findByRole('region', { name: 'Remedy' })
    expect(remedy.textContent).toBe(label)
    expect(remedy.className).toMatch(/pending/)
  })

  it('never shows serial numbers or staff-only replacement fields even if the response carries them', async () => {
    respondWith({
      status: 'Approved',
      remedy: {
        ...replacementRemedy('ReadyForCollection'),
        serialNumber: 'AT10-9001',
        currentUnit: { serialNumber: 'AT10-9001' },
        serialSeen: 'AT10-0001',
        note: 'Unit put aside at the counter.',
        reason: 'The customer lives in the south.',
        contact: '[LOCATION CONTACT]',
        recordedBy: '[AGENT]',
      },
    })
    renderStatus()

    await screen.findByRole('region', { name: 'Remedy' })
    const page = document.body.textContent ?? ''
    expect(page).not.toMatch(/AT10-9001|AT10-0001|Unit put aside|lives in the south|\[LOCATION CONTACT\]|\[AGENT\]/)
  })

  it('never shows the refund reference or who recorded the completion', async () => {
    respondWith({
      status: 'Approved',
      remedy: { remedy: 'Refund', label: 'Refund completed', completed: true, steps: [], reference: 'RF-99881', recordedBy: '[AGENT]' },
    })
    renderStatus()

    const remedy = await screen.findByRole('region', { name: 'Remedy' })
    expect(remedy).toHaveTextContent('Refund completed')
    expect(document.body).not.toHaveTextContent('RF-99881')
    expect(document.body).not.toHaveTextContent('[AGENT]')
  })

  it('says "Repair not possible" after a failed repair in a pending banner, without the failure reason or the centre', async () => {
    respondWith({
      status: 'Approved',
      remedy: {
        label: 'Repair not possible. Your claim is being reviewed.',
        completed: false,
        steps: [],
        repairCentre: { name: 'North Service Hub', address: '12 Harbour Road, Springfield', openingHours: 'Mon–Fri 08:00–17:00' },
        failureReason: 'Main board fault reappeared after the second repair',
      },
    })
    renderStatus()

    const remedy = await screen.findByRole('region', { name: 'Remedy' })
    expect(remedy.textContent).toBe('Repair not possible. Your claim is being reviewed.')
    expect(remedy.className).toMatch(/pending/)
    const page = document.body.textContent ?? ''
    expect(page).not.toMatch(/Main board fault|North Service Hub|Remedy: Repair/)
    expect(screen.getByText('Your claim is approved')).toBeInTheDocument()
  })

  it('never shows staff-only remedy fields even if the response carries them', async () => {
    const remedy = repairRemedy('AtRepairCentre') as Record<string, unknown>
    respondWith({
      status: 'Approved',
      remedy: {
        ...remedy,
        repairCentre: {
          ...(remedy.repairCentre as object),
          reference: 'RC-NA-0042',
          contact: 'workshop@example.test',
          phone: '+1 555 0100',
        },
        notes: 'Customer was rude at the counter',
        failureReason: 'Spare part discontinued',
        recordedBy: 'Agent Smith',
        decidedBy: 'Reviewer Jones',
      },
    })
    renderStatus()

    await screen.findByRole('region', { name: 'Remedy' })
    const page = document.body.textContent ?? ''
    expect(page).not.toMatch(
      /RC-NA-0042|workshop@example\.test|555 0100|Customer was rude|Spare part discontinued|Agent Smith|Reviewer Jones/,
    )
  })

  it('shows the explanation of a rejected claim without a remedy section', async () => {
    respondWith({ status: 'Rejected', outcomeExplanation: 'Water damage is not covered by your warranty.' })
    renderStatus()

    expect(await screen.findByText('Your claim was not approved')).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Claim outcome' })).toHaveTextContent(
      'Water damage is not covered by your warranty.',
    )
    expect(screen.queryByRole('region', { name: 'Remedy' })).not.toBeInTheDocument()
    expect(document.body).not.toHaveTextContent(/service centre will contact you/i)
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
