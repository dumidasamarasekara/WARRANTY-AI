import type { ReactNode } from 'react'
import { cx } from '../cx'
import type { Tone } from '../tone'
import tones from '../tone.module.css'
import styles from './Timeline.module.css'

export interface TimelineProps {
  label?: string
  className?: string
  children: ReactNode
}

/** Vertical rail of `TimelineItem`s. */
export function Timeline({ label, className, children }: TimelineProps) {
  return (
    <ol aria-label={label} className={cx(styles.timeline, className)}>
      {children}
    </ol>
  )
}

export interface TimelineItemProps {
  /** Dot colour; the item content must also say who acted. */
  tone?: Tone
  className?: string
  children: ReactNode
}

export function TimelineItem({ tone = 'system', className, children }: TimelineItemProps) {
  return (
    <li className={cx(styles.item, tones[tone], className)}>
      <span className={styles.dot} aria-hidden="true" />
      <div className={styles.card}>{children}</div>
    </li>
  )
}
