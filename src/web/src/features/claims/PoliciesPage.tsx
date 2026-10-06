import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useMe } from '../../app/staffQueries'
import { api, ApiProblem, unwrap } from '../../shared/api/client'
import { formatDate } from '../../shared/presentation'
import { ActorBadge, Badge, Card, cx, EmptyState, KeyValueList, LoadingState, ProblemState } from '../../shared/ui'
import tones from '../../shared/ui/tone.module.css'
import styles from './PoliciesPage.module.css'
import {
  buildTimeline,
  groupByPolicy,
  isoToday,
  policyVersionStatus,
  policyVersionStatusPresentation,
  type PolicyVersionSummary,
} from './policyVersions'

const versionKey = (version: PolicyVersionSummary) => `${version.policyCode}:${version.version}`

const effectiveRange = (version: PolicyVersionSummary) =>
  `${formatDate(version.effectiveFrom)} – ${version.effectiveTo ? formatDate(version.effectiveTo) : 'open-ended'}`

function StatusBadgeFor({ version, today }: { version: PolicyVersionSummary; today: string }) {
  const { label, tone } = policyVersionStatusPresentation(policyVersionStatus(version, today))
  return (
    <Badge tone={tone} dot>
      {label}
    </Badge>
  )
}

/** Segments per version in proportion to their date ranges; open-ended versions run to today + 6 months. */
function ApplicabilityTimeline({ versions, selected, today }: { versions: PolicyVersionSummary[]; selected: PolicyVersionSummary; today: string }) {
  const timeline = buildTimeline(versions, today)
  if (!timeline) return null
  return (
    <Card title="Applicability timeline" titleAs="h2">
      <ol className={styles.timeline} aria-label="Applicability timeline">
        {timeline.segments.map((segment) => (
          <li
            key={versionKey(segment.version)}
            className={cx(
              styles.segment,
              segment.status === 'Active' && styles.active,
              segment.status === 'Scheduled' && styles.scheduled,
              versionKey(segment.version) === versionKey(selected) && styles.selected,
            )}
            style={{ left: `${segment.start}%`, width: `${segment.width}%` }}
            title={`Policy v${segment.version.version}: ${effectiveRange(segment.version)}`}
          >
            <span className={styles.segmentLabel}>v{segment.version.version}</span>
            <span className="visually-hidden">
              {' '}
              {effectiveRange(segment.version)} ({policyVersionStatusPresentation(segment.status).label})
            </span>
          </li>
        ))}
      </ol>
      <div className={styles.axis} aria-hidden="true">
        <span>{formatDate(timeline.from)}</span>
        <span>{formatDate(timeline.to)}</span>
      </div>
    </Card>
  )
}

/** `/staff/policies` (all staff): the tenant's policy versions and when each applies (ui-design.md §6.5). */
export function PoliciesPage({ today = isoToday() }: { today?: string }) {
  const me = useMe()
  const policies = useQuery({
    queryKey: ['policies'],
    queryFn: ({ signal }) => unwrap(api.GET('/api/policies', { signal })),
  })
  const [selectedKey, setSelectedKey] = useState<string | null>(null)

  const tenant = me.data?.tenantDisplayName
  const header = (
    <div className={styles.intro}>
      <h1>{tenant ? `Warranty policies — ${tenant}` : 'Warranty policies'}</h1>
      <p className={styles.subtitle}>The AI applies the version in force on the purchase date, never the latest by default.</p>
    </div>
  )

  if (policies.isPending) {
    return (
      <section className={styles.page}>
        {header}
        <LoadingState message="Loading policies…" />
      </section>
    )
  }
  if (policies.isError) {
    return (
      <section className={styles.page}>
        {header}
        <ProblemState problem={policies.error instanceof ApiProblem ? policies.error : {}} />
      </section>
    )
  }

  const groups = groupByPolicy(policies.data)
  const all = groups.flatMap((group) => group.versions)
  const selected =
    all.find((version) => versionKey(version) === selectedKey) ??
    all.find((version) => policyVersionStatus(version, today) === 'Active') ??
    all[0]
  if (!selected) {
    return (
      <section className={styles.page}>
        {header}
        <EmptyState message="No warranty policies are configured for this tenant." />
      </section>
    )
  }

  const selectedVersions = all.filter((version) => version.policyCode === selected.policyCode)
  const older = selectedVersions.find((version) => policyVersionStatus(version, today) === 'Superseded')

  return (
    <section className={styles.page}>
      {header}
      <div className={styles.layout}>
        <nav className={styles.versions} aria-label="Policy versions">
          {groups.map((group) => (
            <div key={group.policyCode} className={styles.group}>
              <h2 className={styles.groupTitle}>
                <span className={styles.mono}>{group.policyCode}</span>
                <span className={styles.groupName}>{group.title}</span>
              </h2>
              <ul className={styles.versionList}>
                {group.versions.map((version) => {
                  const key = versionKey(version)
                  const current = key === versionKey(selected)
                  return (
                    <li key={key}>
                      <button
                        type="button"
                        className={cx(styles.version, current && styles.current)}
                        aria-pressed={current}
                        onClick={() => setSelectedKey(key)}
                      >
                        <span className={styles.versionHead}>
                          <span className={styles.versionName}>Policy v{version.version}</span>
                          <StatusBadgeFor version={version} today={today} />
                        </span>
                        <span className={styles.versionDates}>{effectiveRange(version)}</span>
                      </button>
                    </li>
                  )
                })}
              </ul>
            </div>
          ))}
        </nav>
        <div className={styles.detail}>
          <ApplicabilityTimeline versions={selectedVersions} selected={selected} today={today} />
          <Card title={`${selected.title} · v${selected.version}`} titleAs="h2" actions={<StatusBadgeFor version={selected} today={today} />}>
            <KeyValueList
              items={[
                { label: 'Policy', value: selected.policyCode, mono: true },
                { label: 'Regions', value: selected.regions.join(', ') },
                { label: 'Effective from', value: formatDate(selected.effectiveFrom) },
                { label: 'Effective to', value: selected.effectiveTo ? formatDate(selected.effectiveTo) : 'Open-ended' },
              ]}
            />
          </Card>
          <div className={cx(styles.note, tones.ai)} role="note">
            <ActorBadge actor="ai" />
            <p>
              Retrieval filters on purchase date.
              {older && (
                <>
                  {' '}
                  A product bought on {formatDate(older.effectiveFrom)} is assessed under Policy v{older.version} even if reviewed today.
                </>
              )}
            </p>
          </div>
        </div>
      </div>
    </section>
  )
}
