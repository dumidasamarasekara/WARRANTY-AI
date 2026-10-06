import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router'
import { useMe } from '../../app/staffQueries'
import { useStaffSession } from '../../app/useStaffSession'
import { api, ApiProblem, unwrap, type Schemas } from '../../shared/api/client'
import {
  aiDecisionPresentation,
  claimStatusPresentation,
  dispositionPresentation,
  formatDate,
  type ClaimStatus,
} from '../../shared/presentation'
import {
  ActorBadge,
  Badge,
  Card,
  DataTable,
  EmptyState,
  Pagination,
  ProblemState,
  Select,
  StatusBadge,
  type DataTableColumn,
} from '../../shared/ui'
import styles from './ClaimsListPage.module.css'

type ClaimSummary = Schemas['ClaimSummary']

export const claimsPageSize = 25

const statuses: ClaimStatus[] = ['Submitted', 'UnderEvaluation', 'PendingInformation', 'UnderReview', 'Approved', 'Rejected']

const isStatus = (value: string | null): value is ClaimStatus => statuses.includes(value as ClaimStatus)

const claimPath = (claimId: string, tab = '') => `/staff/claims/${encodeURIComponent(claimId)}${tab}`

/** `/staff/claims` (all staff): the tenant's claims with a status filter (ui-design.md §6.2). */
export function ClaimsListPage() {
  const session = useStaffSession()
  const me = useMe()
  const [search, setSearch] = useSearchParams()
  const statusParam = search.get('status')
  const status = isStatus(statusParam) ? statusParam : undefined
  const page = Math.max(1, Number(search.get('page')) || 1)

  const claims = useQuery({
    queryKey: ['claims', { status, page }],
    queryFn: ({ signal }) =>
      unwrap(api.GET('/api/claims', { params: { query: { status, page, pageSize: claimsPageSize } }, signal })),
    placeholderData: keepPreviousData,
  })

  const roles = session?.roles ?? []
  const canCreate = roles.includes('claims-agent')
  const canTrace = roles.includes('claims-reviewer') || roles.includes('auditor')

  const update = (next: { status?: string; page?: number }) => {
    const params = new URLSearchParams(search)
    if (next.status !== undefined) {
      if (next.status) params.set('status', next.status)
      else params.delete('status')
      params.delete('page')
    }
    if (next.page !== undefined) {
      if (next.page > 1) params.set('page', String(next.page))
      else params.delete('page')
    }
    setSearch(params)
  }

  const columns: DataTableColumn<ClaimSummary>[] = [
    {
      key: 'reference',
      header: 'Reference',
      mono: true,
      cell: (claim) => (
        <Link to={claimPath(claim.claimId)} className={styles.reference}>
          {claim.reference}
        </Link>
      ),
    },
    { key: 'product', header: 'Product', cell: (claim) => claim.productName },
    { key: 'status', header: 'Status', cell: (claim) => <StatusBadge status={claim.status} /> },
    {
      key: 'ai',
      header: 'AI decision',
      cell: (claim) =>
        claim.aiDecision ? (
          <span className={styles.aiDecision}>
            <ActorBadge actor="ai" />
            {aiDecisionPresentation(claim.aiDecision).label}
          </span>
        ) : (
          <span className={styles.none}>—</span>
        ),
    },
    {
      key: 'route',
      header: 'Outcome route',
      cell: (claim) => {
        if (!claim.disposition) return <span className={styles.none}>—</span>
        const { label, tone } = dispositionPresentation(claim.disposition)
        return <Badge tone={tone}>{label}</Badge>
      },
    },
    { key: 'submitted', header: 'Submitted', cell: (claim) => formatDate(claim.submittedAt) },
  ]
  if (canTrace) {
    columns.push({
      key: 'trace',
      header: <span className="visually-hidden">Decision trace</span>,
      cell: (claim) => (
        <Link to={claimPath(claim.claimId, '/trace')} aria-label={`Decision trace of ${claim.reference}`}>
          Trace
        </Link>
      ),
    })
  }

  const total = claims.data?.total
  const tenant = me.data?.tenantDisplayName
  const subtitle = [total !== undefined ? `${total.toLocaleString('en-US')} ${total === 1 ? 'claim' : 'claims'}` : undefined, tenant]
    .filter(Boolean)
    .join(' · ')

  return (
    <section className={styles.page}>
      <div className={styles.header}>
        <div className={styles.intro}>
          <h1>Claims</h1>
          {subtitle && <p className={styles.subtitle}>{subtitle}</p>}
        </div>
        {canCreate && (
          <Link to="/staff/claims/new" className={styles.newClaim}>
            New claim
          </Link>
        )}
      </div>
      <div className={styles.filters}>
        <Select label="Status" value={status ?? ''} onChange={(event) => update({ status: event.target.value })}>
          <option value="">All statuses</option>
          {statuses.map((value) => (
            <option key={value} value={value}>
              {claimStatusPresentation(value).label}
            </option>
          ))}
        </Select>
      </div>
      {claims.isError ? (
        <ProblemState problem={claims.error instanceof ApiProblem ? claims.error : {}} />
      ) : (
        <Card className={styles.tableCard}>
          <DataTable
            caption="Claims"
            columns={columns}
            rows={claims.data?.items ?? []}
            rowKey={(claim) => claim.claimId}
            loading={claims.isPending}
            empty={<EmptyState message={status ? 'No claims with this status.' : 'No claims yet.'} />}
          />
          {claims.data && claims.data.total > 0 && (
            <Pagination
              page={claims.data.page}
              pageSize={claims.data.pageSize}
              total={claims.data.total}
              onPageChange={(next) => update({ page: next })}
            />
          )}
        </Card>
      )}
    </section>
  )
}
