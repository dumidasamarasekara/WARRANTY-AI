import type { ReactNode } from 'react'
import { cx } from '../cx'
import tones from '../tone.module.css'
import styles from './FactorRow.module.css'

export type FactorTone = 'ok' | 'warn' | 'err'

const factorSymbols: Record<FactorTone, { symbol: string; text: string }> = {
  ok: { symbol: '✓', text: 'Passed' },
  warn: { symbol: '!', text: 'Needs attention' },
  err: { symbol: '✕', text: 'Failed' },
}

export interface FactorRowProps {
  tone: FactorTone
  /** Screen-reader text for the symbol; defaults to Passed / Needs attention / Failed. */
  statusLabel?: string
  className?: string
  children: ReactNode
}

/** One decision factor or guardrail check: a ✓ / ! / ✕ circle and its explanation. */
export function FactorRow({ tone, statusLabel, className, children }: FactorRowProps) {
  const { symbol, text } = factorSymbols[tone]
  return (
    <div className={cx(styles.row, tones[tone], className)}>
      <span className={styles.icon} aria-hidden="true">
        {symbol}
      </span>
      <div className={styles.text}>
        <span className="visually-hidden">{statusLabel ?? text}: </span>
        {children}
      </div>
    </div>
  )
}
