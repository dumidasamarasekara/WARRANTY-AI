import type { MouseEvent, ReactNode } from 'react'
import { cx } from '../cx'
import { EmptyState, LoadingState } from '../States'
import styles from './DataTable.module.css'

export interface DataTableColumn<T> {
  key: string
  header: ReactNode
  cell: (row: T) => ReactNode
  align?: 'start' | 'end'
  /** CSS width, e.g. `160px`. */
  width?: string
  mono?: boolean
}

export interface DataTableProps<T> {
  columns: readonly DataTableColumn<T>[]
  rows: readonly T[]
  rowKey: (row: T) => string
  /** Visually hidden table caption. */
  caption: string
  selectedKey?: string
  /**
   * Mouse convenience for opening a row. Keyboard users open it through the link in the first
   * cell, so that link must exist whenever this is set.
   */
  onRowClick?: (row: T) => void
  loading?: boolean
  empty?: ReactNode
  className?: string
}

const interactive = 'a, button, input, select, textarea, label'

export function DataTable<T>({
  columns,
  rows,
  rowKey,
  caption,
  selectedKey,
  onRowClick,
  loading = false,
  empty,
  className,
}: DataTableProps<T>) {
  const handleRowClick = (row: T) => (event: MouseEvent<HTMLTableRowElement>) => {
    if ((event.target as HTMLElement).closest(interactive)) return
    onRowClick?.(row)
  }

  return (
    <div className={cx(styles.scroller, className)}>
      <table className={styles.table} aria-busy={loading || undefined}>
        <caption className="visually-hidden">{caption}</caption>
        <thead>
          <tr>
            {columns.map((column) => (
              <th
                key={column.key}
                scope="col"
                className={cx(column.align === 'end' && styles.end)}
                style={column.width ? { width: column.width } : undefined}
              >
                {column.header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {loading ? (
            <tr>
              <td colSpan={columns.length} className={styles.stateCell}>
                <LoadingState />
              </td>
            </tr>
          ) : rows.length === 0 ? (
            <tr>
              <td colSpan={columns.length} className={styles.stateCell}>
                {empty ?? <EmptyState message="Nothing to show yet" />}
              </td>
            </tr>
          ) : (
            rows.map((row) => {
              const key = rowKey(row)
              return (
                <tr
                  key={key}
                  className={cx(onRowClick && styles.clickable, key === selectedKey && styles.selected)}
                  onClick={onRowClick ? handleRowClick(row) : undefined}
                >
                  {columns.map((column) => (
                    <td key={column.key} className={cx(column.align === 'end' && styles.end, column.mono && styles.mono)}>
                      {column.cell(row)}
                    </td>
                  ))}
                </tr>
              )
            })
          )}
        </tbody>
      </table>
    </div>
  )
}
