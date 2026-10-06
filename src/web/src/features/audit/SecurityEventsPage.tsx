import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router'
import { useMe } from '../../app/staffQueries'
import { api, ApiProblem, unwrap, type Schemas } from '../../shared/api/client'
import { formatDateTime } from '../../shared/presentation'
import { Badge, Card, DataTable, EmptyState, Pagination, ProblemState, Select, type DataTableColumn } from '../../shared/ui'
import styles from './SecurityEventsPage.module.css'
import {
  isSecurityEventKind,
  securityEventKindOptionLabel,
  securityEventKindPresentation,
  securityEventKinds,
} from './securityEvents'

type SecurityEvent = Schemas['SecurityEvent']

export const securityEventsPageSize = 50

// Only these fields are rendered: no details or IP addresses, even if a response carried them (FR-041a).
const columns: DataTableColumn<SecurityEvent>[] = [
  {
    key: 'time',
    header: 'Time',
    width: '180px',
    cell: (event) => (
      <time dateTime={event.occurredAt} className={styles.time}>
        {formatDateTime(event.occurredAt)}
      </time>
    ),
  },
  {
    key: 'kind',
    header: 'Kind',
    cell: (event) => {
      const { label, tone } = securityEventKindPresentation(event.kind)
      return (
        <Badge tone={tone} dot>
          {label}
        </Badge>
      )
    },
  },
  { key: 'actor', header: 'Actor', cell: (event) => event.actor },
  {
    key: 'target',
    header: 'Target',
    mono: true,
    cell: (event) => event.target ?? <span className={styles.none}>—</span>,
  },
]

/** `/staff/security-events` (auditor): the tenant's denied or suspicious access attempts (ui-design.md §6.7). */
export function SecurityEventsPage() {
  const me = useMe()
  const [search, setSearch] = useSearchParams()
  const kindParam = search.get('kind')
  const kind = isSecurityEventKind(kindParam) ? kindParam : undefined
  const page = Math.max(1, Number(search.get('page')) || 1)

  const events = useQuery({
    queryKey: ['security-events', { kind, page }],
    queryFn: ({ signal }) =>
      unwrap(api.GET('/api/security-events', { params: { query: { kind, page, pageSize: securityEventsPageSize } }, signal })),
    placeholderData: keepPreviousData,
  })

  const update = (next: { kind?: string; page?: number }) => {
    const params = new URLSearchParams(search)
    if (next.kind !== undefined) {
      if (next.kind) params.set('kind', next.kind)
      else params.delete('kind')
      params.delete('page')
    }
    if (next.page !== undefined) {
      if (next.page > 1) params.set('page', String(next.page))
      else params.delete('page')
    }
    setSearch(params)
  }

  const tenant = me.data?.tenantDisplayName

  return (
    <section className={styles.page}>
      <div className={styles.intro}>
        <h1>{tenant ? `Security events — ${tenant}` : 'Security events'}</h1>
        <p className={styles.subtitle}>
          Denied or suspicious access attempts in your organisation. Entries cannot be changed or deleted.
        </p>
      </div>
      <div className={styles.filters}>
        <Select label="Kind" value={kind ?? ''} onChange={(event) => update({ kind: event.target.value })}>
          <option value="">All kinds</option>
          {securityEventKinds.map((value) => (
            <option key={value} value={value}>
              {securityEventKindOptionLabel(value)}
            </option>
          ))}
        </Select>
      </div>
      {events.isError ? (
        <ProblemState problem={events.error instanceof ApiProblem ? events.error : {}} />
      ) : (
        <Card className={styles.tableCard}>
          <DataTable
            caption="Security events"
            columns={columns}
            rows={events.data?.items ?? []}
            rowKey={(event) => event.id}
            loading={events.isPending}
            empty={<EmptyState message={kind ? 'No security events of this kind.' : 'No security events recorded'} />}
          />
          {events.data && events.data.total > 0 && (
            <Pagination
              page={events.data.page}
              pageSize={events.data.pageSize}
              total={events.data.total}
              onPageChange={(next) => update({ page: next })}
            />
          )}
        </Card>
      )}
    </section>
  )
}
