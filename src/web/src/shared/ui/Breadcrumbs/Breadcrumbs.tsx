import type { ReactNode } from 'react'
import { cx } from '../cx'
import { AnchorLink, type LinkComponent } from '../link'
import styles from './Breadcrumbs.module.css'

export interface BreadcrumbItem {
  label: ReactNode
  /** Omit for items that are not navigable (e.g. the tenant). */
  to?: string
}

export interface BreadcrumbsProps {
  /** Tenant / section / item; the last item is the current page. */
  items: readonly BreadcrumbItem[]
  link?: LinkComponent
  className?: string
}

export function Breadcrumbs({ items, link: Link = AnchorLink, className }: BreadcrumbsProps) {
  return (
    <nav aria-label="Breadcrumb" className={cx(styles.breadcrumbs, className)}>
      <ol className={styles.list}>
        {items.map((item, index) => {
          const last = index === items.length - 1
          return (
            <li key={index} className={styles.item}>
              {index > 0 && (
                <span className={styles.separator} aria-hidden="true">
                  /
                </span>
              )}
              {last ? (
                <span className={styles.current} aria-current="page">
                  {item.label}
                </span>
              ) : item.to ? (
                <Link to={item.to} className={styles.link}>
                  {item.label}
                </Link>
              ) : (
                <span>{item.label}</span>
              )}
            </li>
          )
        })}
      </ol>
    </nav>
  )
}
