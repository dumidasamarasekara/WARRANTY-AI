import type { ReactNode } from 'react'
import { cx } from '../cx'
import styles from './States.module.css'

interface StateProps {
  action?: ReactNode
  className?: string
}

export function EmptyState({ message, action, className }: StateProps & { message: ReactNode }) {
  return (
    <div className={cx(styles.state, className)}>
      <p className={styles.message}>{message}</p>
      {action}
    </div>
  )
}

export function LoadingState({ message = 'Loading…', className }: { message?: ReactNode; className?: string }) {
  return (
    <div className={cx(styles.state, className)} role="status" aria-live="polite">
      <span className={styles.spinner} aria-hidden="true" />
      <p className={styles.message}>{message}</p>
    </div>
  )
}

/** The RFC 9457 fields the SPA shows for a failed request. */
export interface Problem {
  title?: string
  detail?: string
  status?: number
  correlationId?: string
}

/** A failed request: the problem's title and detail, plus the correlation ID for support. */
export function ProblemState({ problem, action, className }: StateProps & { problem: Problem }) {
  return (
    <div className={cx(styles.state, styles.problem, className)} role="alert">
      <p className={styles.title}>{problem.title ?? 'Something went wrong'}</p>
      {problem.detail && <p className={styles.message}>{problem.detail}</p>}
      {problem.correlationId && (
        <p className={styles.correlation}>
          Correlation ID <code className={styles.mono}>{problem.correlationId}</code>
        </p>
      )}
      {action}
    </div>
  )
}
