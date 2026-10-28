import type { Locator, Page } from '@playwright/test'
import { renderProcess } from './process'

/** Multiplies every pause; DEMO_PACE=0.2 for a quick rehearsal, 0.9 (default) for the ~5 minute demo. */
const pace = Number(process.env.DEMO_PACE ?? '0.9')

/** A pause the audience needs to read what is on screen. */
export const beat = (page: Page, ms: number) => page.waitForTimeout(Math.round(ms * pace))

/** Typing speed for visible form filling (ms per key). */
export const keyDelay = Math.round(25 * pace)

export interface Caption {
  /** Short label of the act, e.g. "1 / 4 · Aurora Electronics". */
  step: string
  title: string
  body?: string
  /** Accent of the card: neutral narration, a rejection or an approval. */
  tone?: 'info' | 'reject' | 'approve'
}

/**
 * Shows (or replaces) the caption card at the bottom of the page. Full page loads drop it, so call it
 * again after a navigation that reloads the document (e.g. the Keycloak sign-in).
 */
export async function caption(page: Page, { step, title, body = '', tone = 'info' }: Caption, holdMs = 0) {
  // A full page load drops the process tracker too; put it back with the caption.
  if (!(await page.locator('#demo-process').count())) await renderProcess(page)
  await page.evaluate(
    ({ step, title, body, tone }) => {
      const accents = { info: '#6366f1', reject: '#dc2626', approve: '#16a34a' }
      let card = document.getElementById('demo-caption')
      if (!card) {
        card = document.createElement('div')
        card.id = 'demo-caption'
        card.setAttribute('aria-hidden', 'true')
        Object.assign(card.style, {
          position: 'fixed',
          left: '50%',
          transform: 'translateX(-50%)',
          width: 'min(920px, calc(100vw - 48px))',
          padding: '18px 24px',
          borderRadius: '14px',
          background: 'rgba(15, 23, 42, 0.94)',
          color: '#f8fafc',
          font: '16px/1.45 Inter, "Segoe UI", system-ui, sans-serif',
          boxShadow: '0 18px 50px rgba(0,0,0,0.35)',
          zIndex: '2147483647',
          pointerEvents: 'none',
          transition: 'opacity 200ms ease',
        })
        document.body.appendChild(card)
      }
      // Each new caption starts at the bottom; spotlight() may move it to the top.
      card.style.top = ''
      card.style.bottom = '24px'
      card.style.borderLeft = `6px solid ${accents[tone]}`
      card.innerHTML = ''
      const stepEl = document.createElement('div')
      stepEl.textContent = step
      Object.assign(stepEl.style, { fontSize: '12px', letterSpacing: '0.08em', textTransform: 'uppercase', color: accents[tone], fontWeight: '700' })
      const titleEl = document.createElement('div')
      titleEl.textContent = title
      Object.assign(titleEl.style, { fontSize: '26px', fontWeight: '700', margin: '2px 0 4px', lineHeight: '1.25' })
      card.append(stepEl, titleEl)
      if (body) {
        const bodyEl = document.createElement('div')
        bodyEl.textContent = body
        Object.assign(bodyEl.style, { color: '#cbd5e1', fontSize: '18px' })
        card.append(bodyEl)
      }
    },
    { step, title, body, tone },
  )
  if (holdMs > 0) await beat(page, holdMs)
}

export async function hideCaption(page: Page) {
  await page.evaluate(() => {
    document.getElementById('demo-caption')?.remove()
    document.getElementById('demo-process')?.remove()
  })
}

let snapshotCount = 0

/**
 * Scrolls an element clear of the caption card and rings it so the audience knows where to look.
 * With DEMO_SNAPSHOTS=1 each spotlight is also saved to test-results/snapshots/ for checking a run.
 */
export async function spotlight(locator: Locator, holdMs = 2500, name = 'spotlight') {
  await locator.evaluate((element) => {
    const el = element as HTMLElement
    const tall = el.getBoundingClientRect().height > window.innerHeight * 0.55
    el.scrollIntoView({ block: tall ? 'start' : 'center', behavior: 'smooth' })
    el.dataset.demoOutline = el.style.outline
    el.style.outline = '4px solid #f59e0b'
    el.style.outlineOffset = '4px'
    el.style.borderRadius = el.style.borderRadius || '8px'
  })
  const page = locator.page()
  // Wait for the smooth scroll to settle, then move the caption to the edge that hides less of the element.
  await locator.evaluate(async (element) => {
    let last = Number.NaN
    for (let stable = 0, i = 0; stable < 3 && i < 40; i++) {
      await new Promise((resolve) => setTimeout(resolve, 80))
      const top = element.getBoundingClientRect().top
      stable = top === last ? stable + 1 : 0
      last = top
    }
    const card = document.getElementById('demo-caption')
    if (!card) return
    const rect = element.getBoundingClientRect()
    const band = card.getBoundingClientRect().height + 48
    const hiddenAtBottom = Math.max(0, Math.min(rect.bottom, window.innerHeight) - (window.innerHeight - band))
    const hiddenAtTop = Math.max(0, band - Math.max(rect.top, 0))
    const atTop = hiddenAtTop < hiddenAtBottom
    card.style.top = atTop ? '24px' : ''
    card.style.bottom = atTop ? '' : '24px'
  })
  if (process.env.DEMO_SNAPSHOTS === '1') {
    snapshotCount += 1
    await page.screenshot({ path: `test-results/snapshots/${String(snapshotCount).padStart(2, '0')}-${name}.png` })
  }
  await beat(page, holdMs)
  await locator.evaluate((element) => {
    const el = element as HTMLElement
    el.style.outline = el.dataset.demoOutline ?? ''
  })
}
