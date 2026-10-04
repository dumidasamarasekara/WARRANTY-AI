import type { ReactNode } from 'react'
import { cx } from '../cx'
import type { Tone } from '../tone'
import tones from '../tone.module.css'
import styles from './Badge.module.css'

export interface BadgeProps {
  tone?: Tone
  /** Leading dot in the tone's fill colour; the text still carries the meaning. */
  dot?: boolean
  mono?: boolean
  title?: string
  className?: string
  children: ReactNode
}

export function Badge({ tone = 'system', dot = false, mono = false, title, className, children }: BadgeProps) {
  return (
    <span className={cx(styles.badge, tones[tone], mono && styles.mono, className)} title={title}>
      {dot && <span className={styles.dot} aria-hidden="true" />}
      {children}
    </span>
  )
}
