import type { ReactNode } from 'react'
import { cx } from '../cx'
import styles from './Card.module.css'

export interface CardProps {
  /** Card title (Inter 600 16/24). */
  title?: ReactNode
  /** Uppercase section label, used instead of a title. */
  label?: ReactNode
  /** Right-aligned header actions. */
  actions?: ReactNode
  titleAs?: 'h2' | 'h3' | 'h4'
  /** `md` 16 px, `lg` 20 px. */
  padding?: 'md' | 'lg'
  className?: string
  children?: ReactNode
}

export function Card({ title, label, actions, titleAs: Heading = 'h3', padding = 'md', className, children }: CardProps) {
  const heading = title ?? label
  return (
    <div className={cx(styles.card, styles[padding], className)}>
      {(heading || actions) && (
        <div className={styles.header}>
          {heading && <Heading className={title ? styles.title : styles.label}>{heading}</Heading>}
          {actions && <div className={styles.actions}>{actions}</div>}
        </div>
      )}
      {children}
    </div>
  )
}
