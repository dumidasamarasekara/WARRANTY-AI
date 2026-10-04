import { formatDate } from '../../presentation/format'
import { Badge } from '../Badge'
import { cx } from '../cx'
import styles from './PolicyCitation.module.css'

export interface PolicyCitationProps {
  documentTitle: string
  clauseKey: string
  clauseTitle?: string
  version: number
  effectiveFrom: string
  effectiveTo?: string | null
  excerpt?: string
  /** The tenant the policy belongs to; always shown next to a citation. */
  tenantName: string
  /** Whether the AI cited this clause or it was only retrieved. */
  cited?: boolean
  className?: string
}

export function PolicyCitation({
  documentTitle,
  clauseKey,
  clauseTitle,
  version,
  effectiveFrom,
  effectiveTo,
  excerpt,
  tenantName,
  cited = true,
  className,
}: PolicyCitationProps) {
  return (
    <article className={cx(styles.citation, !cited && styles.notCited, className)} aria-label={`${documentTitle}, ${clauseKey}`}>
      <p className={styles.heading}>
        <span className={styles.document}>{documentTitle}</span>
        <span aria-hidden="true"> · </span>
        <code className={styles.clauseKey}>{clauseKey}</code>
        {clauseTitle && (
          <>
            <span aria-hidden="true"> · </span>
            <span>{clauseTitle}</span>
          </>
        )}
      </p>
      <p className={styles.version}>
        Version {version} · effective {formatDate(effectiveFrom)} – {effectiveTo ? formatDate(effectiveTo) : 'no end date'}
      </p>
      {excerpt && <blockquote className={styles.excerpt}>{excerpt}</blockquote>}
      <div className={styles.chips}>
        <Badge tone={cited ? 'ai' : 'pending'}>{cited ? 'Cited' : 'Retrieved, not cited'}</Badge>
        <Badge tone="system">Effective {formatDate(effectiveFrom)}</Badge>
        <Badge tone="system">{tenantName} only</Badge>
      </div>
    </article>
  )
}
