import { useId, type ReactNode } from 'react'
import { ActorBadge } from '../ActorBadge'
import { Badge } from '../Badge'
import { cx } from '../cx'
import styles from './AiPanel.module.css'

export type AiPanelState = 'recommendation' | 'invalid' | 'failed'

export interface AiPanelProps {
  title: ReactNode
  /** Right-aligned meta, e.g. model · prompt version. */
  meta?: ReactNode
  variant?: 'compact' | 'full'
  /** `invalid`: the output failed validation; `failed`: the AI step did not complete. */
  state?: AiPanelState
  titleAs?: 'h2' | 'h3' | 'h4'
  className?: string
  children?: ReactNode
}

/**
 * Container for AI output: dashed violet border and an `AI` tag so a recommendation never looks
 * final (ui-design.md §1). The region is labelled by its header.
 */
export function AiPanel({
  title,
  meta,
  variant = 'compact',
  state = 'recommendation',
  titleAs: Heading = 'h3',
  className,
  children,
}: AiPanelProps) {
  const tagId = useId()
  const titleId = useId()
  return (
    <section className={cx(styles.panel, styles[variant], className)} aria-labelledby={`${tagId} ${titleId}`}>
      <div className={styles.header}>
        <span id={tagId}>
          <ActorBadge actor="ai" />
        </span>
        <Heading id={titleId} className={styles.title}>
          {title}
        </Heading>
        {state === 'invalid' && <Badge tone="err">Invalid output</Badge>}
        {state === 'failed' && <Badge tone="warn">AI step failed</Badge>}
        {meta && <span className={styles.meta}>{meta}</span>}
      </div>
      <div className={styles.body}>{children}</div>
    </section>
  )
}
