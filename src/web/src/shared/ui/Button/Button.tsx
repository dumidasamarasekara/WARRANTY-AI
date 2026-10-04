import type { ComponentProps } from 'react'
import { cx } from '../cx'
import styles from './Button.module.css'

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger' | 'human'

export interface ButtonProps extends ComponentProps<'button'> {
  /** One `primary` per view; `human` is for override and escalate actions. */
  variant?: ButtonVariant
  /** `md` 36 px (staff), `lg` 40 px (claimant). */
  size?: 'md' | 'lg'
  fullWidth?: boolean
  /** Keeps the label, shows a spinner and blocks repeat clicks. */
  loading?: boolean
}

export function Button({
  variant = 'secondary',
  size = 'md',
  fullWidth = false,
  loading = false,
  disabled,
  type = 'button',
  className,
  children,
  ...rest
}: ButtonProps) {
  return (
    <button
      {...rest}
      type={type}
      className={cx(styles.button, styles[variant], styles[size], fullWidth && styles.fullWidth, className)}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
    >
      {loading && <span className={styles.spinner} aria-hidden="true" />}
      {children}
    </button>
  )
}
