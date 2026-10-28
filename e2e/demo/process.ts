import type { Page } from '@playwright/test'

/** The stages of a claim, in the order the platform runs them. */
export type ProcessStep = 'submit' | 'intake' | 'evidence' | 'policy' | 'decision' | 'guardrails' | 'action' | 'trace' | 'remedy'

export interface ProcessState {
  /** The company whose claim is shown; omitted for the intro. */
  company?: string
  /** The policy rule that decides this case, in plain words. */
  rule?: string
  ruleTone?: 'approve' | 'reject'
  /** Known once the claim is decided. */
  outcome?: 'Approved' | 'Rejected'
  /** The highlighted stage. */
  current?: ProcessStep
  /** `live`: stages before `current` are done, later ones still to come. `review`: every stage is done. */
  mode: 'live' | 'review'
  /** Cycles the highlight through the AI stages while the claim is being processed. */
  working?: boolean
  /** Adds the remedy stage (approved claims). */
  withRemedy?: boolean
}

let state: ProcessState | undefined

/** Shows the process tracker in the free space at the left (the staff sidebar, or the claimant page margin). */
export async function showProcess(page: Page, next: ProcessState) {
  state = next
  await renderProcess(page)
}

/** Draws the current tracker again; full page loads drop it. */
export async function renderProcess(page: Page) {
  if (!state) return
  await page.evaluate((s) => {
    const steps: Array<{ key: string; label: string; sub: string; ai?: boolean }> = [
      { key: 'submit', label: 'Customer submits', sub: 'claim, invoice, photos' },
      { key: 'intake', label: 'Intake agent', sub: 'understands the description', ai: true },
      { key: 'evidence', label: 'Evidence agent', sub: 'checks invoice & photos', ai: true },
      { key: 'policy', label: 'Policy agent', sub: 'finds this company’s rules', ai: true },
      { key: 'decision', label: 'Decision agent', sub: 'recommends approve / reject', ai: true },
      { key: 'guardrails', label: 'Guardrails', sub: 'fixed safety checks' },
      { key: 'action', label: 'Automatic decision', sub: 'only if every check passes' },
      { key: 'trace', label: 'Audit trail', sub: 'every step recorded' },
    ]
    if (s.withRemedy) steps.push({ key: 'remedy', label: 'Remedy', sub: 'repair arranged' })

    const colors = { ai: '#7c3aed', done: '#16a34a', reject: '#dc2626', approve: '#16a34a', muted: '#94a3b8', ink: '#0f172a' }
    const w = window as unknown as { __demoProcessTimer?: number }
    if (w.__demoProcessTimer) clearInterval(w.__demoProcessTimer)

    if (!document.getElementById('demo-process-style')) {
      const style = document.createElement('style')
      style.id = 'demo-process-style'
      style.textContent =
        '@keyframes demo-pulse{0%{box-shadow:0 0 0 0 rgba(245,158,11,.55)}70%{box-shadow:0 0 0 9px rgba(245,158,11,0)}100%{box-shadow:0 0 0 0 rgba(245,158,11,0)}}'
      document.head.appendChild(style)
    }
    let panel = document.getElementById('demo-process')
    if (!panel) {
      panel = document.createElement('aside')
      panel.id = 'demo-process'
      panel.setAttribute('aria-hidden', 'true')
      Object.assign(panel.style, {
        position: 'fixed',
        left: '12px',
        top: '206px',
        width: '216px',
        padding: '14px 14px 12px',
        borderRadius: '12px',
        background: '#ffffff',
        border: '1px solid #e2e8f0',
        boxShadow: '0 10px 30px rgba(15,23,42,0.12)',
        font: '13px/1.3 Inter, "Segoe UI", system-ui, sans-serif',
        color: colors.ink,
        zIndex: '2147483646',
        pointerEvents: 'none',
      })
      document.body.appendChild(panel)
    }

    const draw = (currentKey: string | undefined) => {
      panel!.innerHTML = ''
      const head = document.createElement('div')
      head.innerHTML = `<div style="font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:#64748b;font-weight:700">How the decision is made</div>`
      if (s.company) {
        const company = document.createElement('div')
        company.textContent = s.company
        Object.assign(company.style, { fontSize: '16px', fontWeight: '700', marginTop: '4px' })
        head.appendChild(company)
      }
      if (s.rule) {
        const rule = document.createElement('div')
        rule.textContent = s.rule
        const tone = s.ruleTone === 'approve' ? colors.approve : colors.reject
        Object.assign(rule.style, {
          marginTop: '6px',
          padding: '5px 8px',
          borderRadius: '6px',
          fontSize: '12px',
          fontWeight: '600',
          color: tone,
          background: s.ruleTone === 'approve' ? '#f0fdf4' : '#fef2f2',
          border: `1px solid ${tone}33`,
        })
        head.appendChild(rule)
      }
      panel!.appendChild(head)

      const list = document.createElement('ol')
      Object.assign(list.style, { listStyle: 'none', margin: '10px 0 0', padding: '0' })
      const currentIndex = steps.findIndex((step) => step.key === currentKey)
      steps.forEach((step, index) => {
        const isCurrent = index === currentIndex
        const done = s.mode === 'review' || (currentIndex >= 0 && index < currentIndex)
        const isOutcome = step.key === 'action' && s.outcome && (done || isCurrent)
        const outcomeColor = s.outcome === 'Approved' ? colors.approve : colors.reject

        const item = document.createElement('li')
        Object.assign(item.style, {
          display: 'flex',
          gap: '9px',
          alignItems: 'flex-start',
          padding: '5px 6px',
          margin: '0 -6px',
          borderRadius: '8px',
          background: isCurrent ? '#fffbeb' : 'transparent',
          outline: isCurrent ? '2px solid #f59e0b' : 'none',
          opacity: done || isCurrent ? '1' : '0.5',
        })
        const dot = document.createElement('span')
        dot.textContent = isOutcome ? (s.outcome === 'Approved' ? '✓' : '✕') : done ? '✓' : String(index + 1)
        const dotColor = isOutcome ? outcomeColor : isCurrent ? '#f59e0b' : done ? colors.done : colors.muted
        Object.assign(dot.style, {
          flex: '0 0 22px',
          height: '22px',
          borderRadius: '50%',
          display: 'grid',
          placeItems: 'center',
          fontSize: '12px',
          fontWeight: '700',
          color: '#fff',
          background: dotColor,
          animation: isCurrent ? 'demo-pulse 1.4s infinite' : 'none',
        })
        const text = document.createElement('span')
        const label = isOutcome ? `${s.outcome} automatically` : step.label
        text.innerHTML =
          `<span style="display:block;font-weight:650;color:${isOutcome ? outcomeColor : colors.ink}">${label}` +
          (step.ai ? ` <span style="font-size:10px;font-weight:700;color:#fff;background:${colors.ai};border-radius:4px;padding:1px 4px;vertical-align:1px">AI</span>` : '') +
          `</span><span style="display:block;font-size:11.5px;color:#64748b">${step.sub}</span>`
        item.append(dot, text)
        list.appendChild(item)
      })
      panel!.appendChild(list)
    }

    if (s.working) {
      // While the AI processes the claim, walk the highlight through the AI stages.
      const aiSteps = ['intake', 'evidence', 'policy', 'decision', 'guardrails']
      let i = 0
      draw(aiSteps[0])
      w.__demoProcessTimer = window.setInterval(() => {
        i = (i + 1) % aiSteps.length
        draw(aiSteps[i])
      }, 900)
    } else {
      draw(s.current)
    }
  }, state)
}
