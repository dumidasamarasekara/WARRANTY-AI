import type { ReactNode } from 'react'
import { cx } from '../cx'
import styles from './KeyValueList.module.css'

export interface KeyValueItem {
  key?: string
  label: ReactNode
  value: ReactNode
  /** IDs, model codes and serials. */
  mono?: boolean
}

export function KeyValueList({ items, className }: { items: readonly KeyValueItem[]; className?: string }) {
  return (
    <dl className={cx(styles.list, className)}>
      {items.map((item, index) => (
        <div key={item.key ?? index} className={styles.row}>
          <dt className={styles.label}>{item.label}</dt>
          <dd className={cx(styles.value, item.mono && styles.mono)}>{item.value}</dd>
        </div>
      ))}
    </dl>
  )
}
