import { Button } from '../Button'
import { cx } from '../cx'
import styles from './Pagination.module.css'

export interface PaginationProps {
  /** 1-based page number. */
  page: number
  pageSize: number
  total: number
  onPageChange: (page: number) => void
  className?: string
}

export function Pagination({ page, pageSize, total, onPageChange, className }: PaginationProps) {
  const first = total === 0 ? 0 : (page - 1) * pageSize + 1
  const last = Math.min(page * pageSize, total)
  const format = (value: number) => value.toLocaleString('en-US')
  return (
    <nav aria-label="Pagination" className={cx(styles.pagination, className)}>
      <p className={styles.summary}>
        {total === 0 ? 'Showing 0 of 0' : `Showing ${format(first)}–${format(last)} of ${format(total)}`}
      </p>
      <div className={styles.buttons}>
        <Button disabled={page <= 1} onClick={() => onPageChange(page - 1)}>
          Previous
        </Button>
        <Button disabled={last >= total} onClick={() => onPageChange(page + 1)}>
          Next
        </Button>
      </div>
    </nav>
  )
}
