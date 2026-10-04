import type { ReactNode } from 'react'
import { cx } from '../cx'
import type { Tone } from '../tone'
import tones from '../tone.module.css'
import styles from './Alert.module.css'

export type AlertTone = 'ok' | 'warn' | 'err' | 'info'

const alertTones: Record<AlertTone, { tone: Tone; symbol: string }> = {
  ok: { tone: 'ok', symbol: '✓' },
  warn: { tone: 'warn', symbol: '!' },
  err: { tone: 'err', symbol: '✕' },
  info: { tone: 'human', symbol: 'i' },
}

export interface AlertProps {
  tone?: AlertTone
  title?: ReactNode
  /** `role="alert"`: use for errors caused by the user's own action; otherwise `role="status"`. */
  urgent?: boolean
  action?: ReactNode
  className?: string
  children?: ReactNode
}

export function Alert({ tone = 'info', title, urgent = false, action, className, children }: AlertProps) {
  const { tone: toneClass, symbol } = alertTones[tone]
  return (
    <div role={urgent ? 'alert' : 'status'} className={cx(styles.alert, tones[toneClass], className)}>
      <span className={styles.symbol} aria-hidden="true">
        {symbol}
      </span>
      <div className={styles.content}>
        {title && <p className={styles.title}>{title}</p>}
        {children && <div className={styles.body}>{children}</div>}
      </div>
      {action && <div className={styles.action}>{action}</div>}
    </div>
  )
}
