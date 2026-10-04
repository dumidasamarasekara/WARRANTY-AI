import type { ReactNode } from 'react'
import { cx } from '../cx'
import { AnchorLink, type LinkComponent } from '../link'
import styles from './Tabs.module.css'

export interface TabItem {
  key: string
  label: ReactNode
  to: string
}

export interface TabsProps {
  items: readonly TabItem[]
  currentKey: string
  /** Accessible name of the tab navigation, e.g. "Claim sections". */
  label: string
  link?: LinkComponent
  className?: string
}

/** Route tabs: links with `aria-current="page"` on the current one, underlined in primary. */
export function Tabs({ items, currentKey, label, link: Link = AnchorLink, className }: TabsProps) {
  return (
    <nav aria-label={label} className={cx(styles.tabs, className)}>
      <ul className={styles.list}>
        {items.map((item) => {
          const current = item.key === currentKey
          return (
            <li key={item.key}>
              <Link to={item.to} className={cx(styles.tab, current && styles.current)} aria-current={current ? 'page' : undefined}>
                {item.label}
              </Link>
            </li>
          )
        })}
      </ul>
    </nav>
  )
}
