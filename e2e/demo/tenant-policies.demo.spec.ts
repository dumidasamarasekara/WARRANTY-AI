import { readFileSync } from 'node:fs'
import path from 'node:path'
import { expect, test, type Locator, type Page } from '@playwright/test'
import { beat, caption, hideCaption, keyDelay, spotlight, type Caption } from './narration'
import { existingDemoClaims } from './preflight'
import { showProcess, type ProcessState, type ProcessStep } from './process'

/**
 * Demo (~5½ min at the default DEMO_PACE): the same accidental screen crack on a 10-month-old tablet, claimed
 * at two companies that run on the same platform with different warranty policies — first as the customers,
 * then inside each company's back office, step by step through how the AI reached its decision.
 *
 *   Aurora Electronics  AUR-TAB10    policy AUR-WP excludes accidental damage (AUR-WP-3.1) → Rejected
 *   Borealis Devices    BOR-SLATE11  Borealis Care covers one incident in 12 months (BOR-WP-1.2) → Approved
 *
 * Captions are kept short (a few-word title, one short line) so they can be read while the demo plays.
 *
 * The claims are golden scenarios S4-aurora / S4-borealis (seed/golden/scenarios.json), so in replay mode
 * the AI answers come from tests/fixtures/ai-recordings/S4-*: no API key and no cost. Each serial can be
 * demonstrated once per database: a second claim on the same serial is flagged as a duplicate and goes to
 * human review instead (see README.md for the reset).
 */

const repoRoot = path.resolve(__dirname, '..', '..')
const staffUrl = process.env.DEMO_STAFF_URL ?? 'http://localhost:5173'
const password = process.env.DEMO_PASSWORD ?? 'Warranty-dev-1!'

interface Scenario {
  scenarioId: string
  tenant: string
  purchaseDateOffsetMonths: number
  claim: {
    customer: Record<'fullName' | 'email' | 'phone' | 'addressLine' | 'city' | 'postalCode' | 'country', string>
    product: { modelCode: string; serialNumber: string }
    purchase: { place: string; price: number; currency: string; country: string }
    problemDescription: string
  }
  evidence: { invoice: string; photos: string[] }
  expected: { status: 'Approved' | 'Rejected'; citedClauseKeys: string[] }
}

function loadScenario(id: string): Scenario {
  const raw = JSON.parse(readFileSync(path.join(repoRoot, 'seed', 'golden', 'scenarios.json'), 'utf8')) as
    | Scenario[]
    | { scenarios: Scenario[] }
  const all = Array.isArray(raw) ? raw : raw.scenarios
  const found = all.find((s) => s.scenarioId === id)
  if (!found) throw new Error(`Scenario ${id} is not in seed/golden/scenarios.json`)
  return found
}

/** Today shifted by whole months (clamped to the month's last day, like .NET AddMonths), as yyyy-MM-dd. */
function monthsFromToday(offset: number): string {
  const today = new Date()
  const target = new Date(today.getFullYear(), today.getMonth() + offset, 1)
  const lastDay = new Date(target.getFullYear(), target.getMonth() + 1, 0).getDate()
  target.setDate(Math.min(today.getDate(), lastDay))
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${target.getFullYear()}-${pad(target.getMonth() + 1)}-${pad(target.getDate())}`
}

const escapeRegExp = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')

/** A form control by its visible label, ignoring the required-field asterisk. */
const field = (page: Page, label: string) => page.getByLabel(new RegExp(`^${escapeRegExp(label)}(\\s*\\*)?$`))

async function typeInto(page: Page, label: string, value: string) {
  const input = field(page, label)
  await input.click()
  await input.pressSequentially(value, { delay: keyDelay })
}

/** A design-system Card (div > header div > heading) by its title. */
const card = (page: Page, title: string) =>
  page.getByRole('heading', { name: title, exact: true }).first().locator('xpath=ancestor::div[2]')

/** An AI panel; its accessible name is the "AI" actor badge followed by the title. */
const aiPanel = (page: Page, title: string) => page.getByRole('region', { name: new RegExp(`${escapeRegExp(title)}$`) }).first()

const approved = (s: Scenario) => s.expected.status === 'Approved'
const companyName = (s: Scenario) => (s.tenant === 'aurora' ? 'Aurora Electronics' : 'Borealis Devices')
const shortName = (s: Scenario) => (s.tenant === 'aurora' ? 'Aurora' : 'Borealis')
const outcomeTitle = (s: Scenario) => (approved(s) ? 'Your claim is approved' : 'Your claim was not approved')
const tone = (s: Scenario): 'approve' | 'reject' => (approved(s) ? 'approve' : 'reject')

/** The process tracker's fixed part for a company: who, and the one policy rule that decides this claim. */
const processOf = (s: Scenario): Pick<ProcessState, 'company' | 'rule' | 'ruleTone' | 'withRemedy'> => ({
  company: companyName(s),
  rule: approved(s) ? 'Policy: accidental damage covered (once in 12 months)' : 'Policy: accidental damage NOT covered',
  ruleTone: tone(s),
  withRemedy: approved(s),
})

/**
 * The guardrail checks in plain words (GuardrailCheckCode). Every check must pass before the system may
 * approve or reject on its own; any failure sends the claim to a person.
 */
const guardrailWords: Record<string, [title: string, line: string]> = {
  SCHEMA_VALID: ['AI answer is well-formed', 'Broken output never acts.'],
  REFERENCES_VALID: ['Citations are real', 'Only evidence and clauses it was given.'],
  REQUIRED_INFO_COMPLETE: ['Nothing missing', 'Invoice, photos and serial are all there.'],
  PRODUCT_IN_CATALOG: ['Known product', 'The model is in this company’s catalog.'],
  POLICY_APPLICABLE: ['Right policy version', 'The version valid on the purchase date.'],
  COVERAGE_WINDOW_AGREES: ['Dates add up', 'AI matches the calculated warranty period.'],
  CLAIM_VALUE_WITHIN_LIMIT: ['Value under the limit', 'Expensive claims go to a person.'],
  CONFIDENCE_AT_OR_ABOVE_MIN: ['AI is sure enough', 'Low confidence goes to a person.'],
  RISK_LOW: ['No risk signals', 'Any fraud signal goes to a person.'],
  NO_CONFLICTS: ['Evidence agrees', 'Invoice, photos and claim tell one story.'],
  NO_MANIPULATION: ['No tricks', 'No hidden instructions in the uploads.'],
  CATEGORY_NOT_ALWAYS_REVIEW: ['Category allowed', 'Some products always need a person.'],
  GROUNDED_IN_CLAUSE: ['Backed by a clause', 'A rejection must cite real policy text.'],
  AUTO_DECISION_ENABLED: ['Automation switched on', 'This company allows automatic decisions.'],
  ACTOR_AUTHORIZED: ['Allowed to act', 'The system has the right to decide.'],
  NOT_RETURNED_FROM_REVIEW: ['No reviewer involved', 'Once a person steps in, a person finishes.'],
  AUTO_INFO_REQUESTS_WITHIN_LIMIT: ['Not asking too often', 'At most two automatic requests.'],
  CLAIMANT_TEXT_SAFE: ['Customer message is safe', 'No fraud words, no internal IDs.'],
  PROTECTION_PLAN_NOT_RELIED_ON: ['No unchecked extras', 'Protection plans need a person.'],
}

/** Shows a caption, then rings the element while the audience reads it. */
async function explain(page: Page, target: Locator, text: Caption, holdMs: number, name: string) {
  await caption(page, text)
  await spotlight(target, holdMs, name)
}

// ---------------------------------------------------------------------------------------------------------
// Customer side
// ---------------------------------------------------------------------------------------------------------

/** A claimant files the claim on the company's own portal and checks its outcome. Returns the claim reference. */
async function fileClaim(page: Page, s: Scenario, step: string): Promise<string> {
  const { customer, product, purchase } = s.claim
  await page.goto(`http://${s.tenant}.localhost:5173/`)
  await expect(page.getByRole('heading', { name: 'Submit a warranty claim' })).toBeVisible()
  await showProcess(page, { ...processOf(s), mode: 'live', current: 'submit' })
  await caption(page, { step, title: `${shortName(s)} customer files a claim`, body: 'Own company portal. No account needed.' }, 3000)

  // Personal details appear instantly; the product details are typed so the audience can follow them.
  await field(page, 'Full name').fill(customer.fullName)
  await field(page, 'Email address').fill(customer.email)
  await field(page, 'Phone number').fill(customer.phone)
  await field(page, 'City').fill(customer.city)
  await field(page, 'Country').fill(customer.country)
  await typeInto(page, 'Model code', product.modelCode)
  await typeInto(page, 'Serial number', product.serialNumber)
  await field(page, 'Purchase date').fill(monthsFromToday(s.purchaseDateOffsetMonths))
  await field(page, 'Place of purchase').fill(purchase.place)
  await field(page, 'Price paid').fill(purchase.price.toFixed(2))
  await field(page, 'Currency').fill(purchase.currency)
  await field(page, 'Country of purchase').fill(purchase.country)
  await caption(page, {
    step,
    title: `Bought ${Math.abs(s.purchaseDateOffsetMonths)} months ago`,
    body: 'Inside the 12-month warranty at both companies.',
  })
  await beat(page, 3000)
  await page.getByRole('button', { name: 'Continue' }).click()

  await expect(page.getByRole('heading', { name: 'What went wrong and your files' })).toBeVisible()
  await page.getByLabel(/^What went wrong\?/).fill(s.claim.problemDescription)
  const evidence = (file: string) => path.join(repoRoot, 'seed', 'evidence', file)
  await page.getByLabel(/^Invoice/).setInputFiles(evidence(s.evidence.invoice))
  await page.getByLabel(/^Photos of the product/).setInputFiles(s.evidence.photos.map(evidence))
  await caption(page, { step, title: '“I dropped it. The screen cracked.”', body: 'Plus the invoice and two photos.' })
  await beat(page, 3500)
  await page.getByRole('button', { name: 'Submit claim' }).click()

  await expect(page.getByRole('heading', { name: 'Claim submitted' })).toBeVisible()
  const reference = (await page.locator('p:has-text("Your claim reference") + p code').textContent())?.trim() ?? ''
  expect(reference, 'the success card shows the claim reference').not.toBe('')
  await page.getByRole('button', { name: 'Check status' }).click()
  await field(page, 'Email address or phone number').fill(customer.email)
  await page.getByRole('button', { name: 'View claim' }).click()
  await showProcess(page, { ...processOf(s), mode: 'live', working: true })
  await caption(page, { step, title: 'The AI agents get to work…', body: 'Read · check · find the policy · recommend · verify.' })

  const outcome = page.getByRole('region', { name: 'Claim outcome' })
  await expect(
    outcome.getByRole('heading'),
    `${s.scenarioId}: expected "${outcomeTitle(s)}". If it stays under review, serial ${product.serialNumber} ` +
      'was already claimed in this database (duplicate check) — reset the database, see README.md.',
  ).toHaveText(outcomeTitle(s), { timeout: 180_000 })
  await showProcess(page, { ...processOf(s), mode: 'live', current: 'action', outcome: s.expected.status })
  await explain(
    page,
    outcome,
    {
      step,
      tone: tone(s),
      title: approved(s) ? `${shortName(s)}: APPROVED in seconds` : `${shortName(s)}: NOT APPROVED in seconds`,
      body: 'A clear reason for the customer. No internal details.',
    },
    5000,
    `${s.tenant}-claimant-outcome`,
  )
  return reference
}

// ---------------------------------------------------------------------------------------------------------
// Company back office
// ---------------------------------------------------------------------------------------------------------

async function signInAsStaff(page: Page, username: string, step: string) {
  // A different company's staff member: drop the previous Keycloak session and the SPA's stored user.
  await page.context().clearCookies()
  // A static file of the SPA's origin: /staff would start a sign-in redirect while we clear its storage.
  await page.goto(`${staffUrl}/favicon.svg`)
  await page.evaluate(() => {
    sessionStorage.clear()
    localStorage.clear()
  })
  await page.goto(`${staffUrl}/staff`)
  await page.locator('#username').waitFor()
  await caption(page, { step, title: `Staff sign in: ${username}`, body: 'The login decides which company they see.' })
  await page.locator('#username').fill(username)
  await page.locator('#password').fill(password)
  await beat(page, 2000)
  await page.locator('#kc-login').click()
  await page.waitForURL(/localhost:5173\/staff/)
}

/** Walks through every guardrail check, one row at a time, in plain words. */
async function explainEachGuardrail(page: Page, factors: Locator, step: string) {
  const codes = await factors.locator('code').allTextContents()
  for (const [index, code] of codes.entries()) {
    const [title, line] = guardrailWords[code] ?? [code, '']
    const row = factors.locator('code', { hasText: new RegExp(`^${escapeRegExp(code)}$`) }).first().locator('xpath=ancestor::div[2]')
    await caption(page, { step: `${step} · check ${index + 1} of ${codes.length}`, title: `✓ ${title}`, body: line, tone: 'approve' })
    await spotlight(row, 1500, `check-${code.toLowerCase()}`)
  }
}

/** The company's reviewer follows the claim through every AI step to the automatic decision. */
async function showBackOffice(page: Page, s: Scenario, reference: string, step: string, detailedChecks: boolean) {
  const say = (title: string, body: string, accent: Caption['tone'] = 'info'): Caption => ({ step, title, body, tone: accent })
  const clause = s.expected.citedClauseKeys[0]!
  // The claim is decided: every stage is done; the tracker highlights the one being shown.
  const at = (current?: ProcessStep) => showProcess(page, { ...processOf(s), mode: 'review', outcome: s.expected.status, current })

  await at()
  await signInAsStaff(page, `reviewer.${s.tenant}`, step)
  await at('action')

  // Claims list
  await page.getByRole('link', { name: 'Claims', exact: true }).first().click()
  const link = page.getByRole('link', { name: reference, exact: true })
  await expect(link).toBeVisible()
  await explain(
    page,
    page.getByRole('row').filter({ has: link }),
    say(`${shortName(s)} back office`, `Only ${shortName(s)}’s claims. The other company is invisible.`),
    5000,
    `${s.tenant}-claims-list`,
  )
  await link.click()

  // Case file
  const header = page.getByRole('heading', { level: 1 }).first().locator('xpath=ancestor::div[3]')
  await expect(header).toContainText(s.expected.status)
  await explain(page, header, say(`${s.expected.status} by the system`, 'No person needed. Every stage done.', tone(s)), 4500, `${s.tenant}-case-header`)
  await at('intake')
  await explain(
    page,
    aiPanel(page, 'Extracted from the description'),
    say('Intake agent reads the story', 'Cause found: an accidental drop.'),
    5500,
    `${s.tenant}-intake`,
  )

  // Evidence
  await page.locator('a[href$="/evidence"]').first().click()
  await at('evidence')
  const files = page.getByRole('navigation', { name: 'Evidence files' })
  await files.getByRole('button', { name: /invoice/i }).first().click()
  await explain(
    page,
    card(page, 'AI-extracted'),
    say('Evidence agent reads the invoice', 'Every field matches the claim.'),
    5500,
    `${s.tenant}-invoice`,
  )
  await files.getByRole('button', { name: /photo-1/i }).first().click()
  await explain(
    page,
    page.getByRole('region', { name: 'AI interpretation' }),
    say('Evidence agent checks the photos', 'Cracked screen. Matches the story.'),
    5500,
    `${s.tenant}-photo`,
  )

  // AI decision
  await page.locator('a[href$="/decision"]').first().click()
  await at('decision')
  const decision = aiPanel(page, 'AI decision')
  await expect(decision).toContainText(approved(s) ? /approve/i : /reject/i)
  await explain(
    page,
    decision.locator('xpath=./div[2]/div[1]'),
    say(`AI recommends: ${approved(s) ? 'APPROVE' : 'REJECT'}`, `Confident. Low risk. ${approved(s) ? 'Covered' : 'Not covered'}.`, tone(s)),
    5000,
    `${s.tenant}-recommendation`,
  )
  await at('guardrails')
  const factors = page.getByRole('region', { name: 'Why — decision factors' })
  await explain(page, factors.locator('p').first(), say('The AI explains why', 'In plain words, with evidence references.'), 6000, `${s.tenant}-reasoning`)
  if (detailedChecks) {
    await caption(page, say('Then fixed safety checks', 'The AI can’t argue with these.'), 3000)
    await explainEachGuardrail(page, factors, step)
  } else {
    await explain(page, factors, say('Same safety checks', 'All passed — so the system may act.', 'approve'), 5000, `${s.tenant}-factors`)
  }

  // Policy
  const citation = page.locator(`article[aria-label$=", ${clause}"]`)
  await expect(citation).toContainText('Cited')
  await at('policy')
  await explain(
    page,
    citation,
    say(
      `Policy agent cites ${clause}`,
      approved(s) ? 'Accidental damage: covered once in 12 months.' : 'Accidental damage: not covered.',
      tone(s),
    ),
    6500,
    `${s.tenant}-citation`,
  )
  await explain(
    page,
    card(page, 'Policy evidence'),
    say(`Only ${shortName(s)}’s policy searched`, 'The other company’s rules never count.'),
    5000,
    `${s.tenant}-policy-evidence`,
  )
  await at('action')
  await explain(
    page,
    card(page, 'Action'),
    say(`System ${approved(s) ? 'approves' : 'rejects'}`, 'Only because every check passed.', tone(s)),
    4500,
    `${s.tenant}-action`,
  )

  // Decision trace
  await page.locator('a[href$="/trace"]').first().click()
  await at('trace')
  const trail = page.getByRole('list', { name: 'Decision trail' }).or(page.locator('[aria-label="Decision trail"]')).first()
  await expect(trail).toBeVisible()
  await explain(
    page,
    page.getByRole('region', { name: 'Decision trace' }).locator('header'),
    say('Every step recorded', 'Tamper-proof: the hash chain is verified.'),
    4500,
    `${s.tenant}-trace-header`,
  )
  await caption(page, say('Who did what, and when', 'AI, system and people in one timeline.'))
  await page.mouse.wheel(0, 500)
  await beat(page, 4000)
  await page.getByRole('group', { name: 'View' }).getByRole('button', { name: 'Technical' }).click()
  await explain(
    page,
    page.getByRole('region', { name: 'RAG queries' }).first(),
    say('Search limited to this company', `Only tenant-${s.tenant} was searched.`),
    5500,
    `${s.tenant}-rag`,
  )
  await explain(
    page,
    page.getByRole('region', { name: 'AI calls' }).first(),
    say('Every AI call measured', 'Model, tokens, time and cost.'),
    5000,
    `${s.tenant}-ai-calls`,
  )
}

test('same damage, two companies, two policies: Aurora rejects, Borealis approves', async ({ page }) => {
  const aurora = loadScenario('S4-aurora')
  const borealis = loadScenario('S4-borealis')
  expect(aurora.expected.status).toBe('Rejected')
  expect(borealis.expected.status).toBe('Approved')

  // Fail before the audience sees anything if an earlier run already used the demo serials.
  const earlier = existingDemoClaims(repoRoot, [aurora.claim.product.serialNumber, borealis.claim.product.serialNumber])
  if (earlier === undefined) console.warn('Preflight: could not check the database for earlier demo claims; continuing.')
  expect(
    earlier ?? 0,
    'An earlier demo run already filed these claims, so they would go to human review (duplicate check). ' +
      'Reset first: npm --prefix e2e/demo run reset',
  ).toBe(0)

  // Full screen for the audience (Edge ignores --start-fullscreen, so ask the window through DevTools).
  if (process.env.DEMO_HEADLESS !== '1') {
    const cdp = await page.context().newCDPSession(page)
    const { windowId } = await cdp.send('Browser.getWindowForTarget')
    await cdp.send('Browser.setWindowBounds', { windowId, bounds: { windowState: 'fullscreen' } })
  }

  // Intro
  await page.goto(`http://aurora.localhost:5173/`)
  await showProcess(page, { mode: 'live' })
  await caption(page, { step: 'WarrantyOS · AI claim decisions', title: 'Same damage. Two companies.', body: 'Two policies — will the AI decide differently?' }, 5000)
  await caption(page, { step: 'WarrantyOS · AI claim decisions', title: 'A dropped tablet, cracked screen', body: 'Bought 10 months ago. Claimed at both.' }, 5000)

  // The customers
  const auroraRef = await fileClaim(page, aurora, '1 / 4 · Aurora · customer')
  const borealisRef = await fileClaim(page, borealis, '2 / 4 · Borealis · customer')

  // The back offices: every check explained once (Aurora), summarised the second time (Borealis)
  await showBackOffice(page, aurora, auroraRef, '3 / 4 · Aurora · back office', true)
  await showBackOffice(page, borealis, borealisRef, '4 / 4 · Borealis · back office', false)

  // Borealis: what happens after an approval
  await page.locator('a[href$="/remedy"]').first().click()
  await showProcess(page, { ...processOf(borealis), mode: 'review', outcome: 'Approved', current: 'remedy' })
  const remedyStep = '4 / 4 · Borealis · back office'
  await explain(
    page,
    card(page, 'Remedy'),
    { step: remedyStep, tone: 'approve', title: 'Approved → repair', body: 'The policy picks the remedy.' },
    5000,
    'borealis-remedy',
  )
  await explain(
    page,
    // "Repair" is also the remedy's name inside the Remedy card; the Repair card comes after it.
    page.getByRole('heading', { name: 'Repair', exact: true }).last().locator('xpath=ancestor::div[2]'),
    { step: remedyStep, tone: 'approve', title: 'Repair centre suggested', body: 'Staff track it. The customer sees progress.' },
    5000,
    'borealis-repair',
  )

  // Finale
  const summary = 'Summary'
  await caption(page, { step: summary, title: 'Aurora: rejected · Borealis: approved', body: 'Same damage. Different policy. Right answer each time.' }, 5000)
  await caption(page, { step: summary, title: 'AI recommends. Rules decide.', body: 'Own policy only · exact clause · safety checks · full record.' }, 7000)
  await hideCaption(page)
})
