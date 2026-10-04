import type { ReactNode } from 'react'
import { ActorBadge } from '../ActorBadge'
import { cx } from '../cx'
import tones from '../tone.module.css'
import styles from './DispositionBanner.module.css'

export interface DispositionBannerProps {
  /** `human`: escalated to a person (solid); `ai`: an automated outcome (dashed). */
  variant: 'human' | 'ai'
  title: ReactNode
  /** Accessible name of the region; defaults to the title when it is plain text. */
  label?: string
  reasons?: readonly ReactNode[]
  /** Replaces the default actor tag. */
  tag?: ReactNode
  className?: string
  children?: ReactNode
}

/** The guardrails' outcome route with its reasons. Informational, not an error alert. */
export function DispositionBanner({ variant, title, label, reasons, tag, className, children }: DispositionBannerProps) {
  return (
    <section
      role="region"
      aria-label={label ?? (typeof title === 'string' ? title : 'Outcome route')}
      className={cx(styles.banner, styles[variant], tones[variant], className)}
    >
      <div className={styles.tag}>{tag ?? <ActorBadge actor={variant} />}</div>
      <div className={styles.content}>
        <p className={styles.title}>{title}</p>
        {reasons && reasons.length > 0 && (
          <ul className={styles.reasons}>
            {reasons.map((reason, index) => (
              <li key={index}>{reason}</li>
            ))}
          </ul>
        )}
        {children}
      </div>
    </section>
  )
}
