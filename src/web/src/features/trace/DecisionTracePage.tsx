import { useState } from 'react'
import { useSearchParams } from 'react-router'
import { ApiProblem } from '../../shared/api/client'
import { formatDateTime, formatTraceTime, trailActorTone, trailDotTone, trailStepLabel } from '../../shared/presentation'
import { ActorBadge, Alert, Badge, cx, EmptyState, LoadingState, ProblemState, Timeline, TimelineItem } from '../../shared/ui'
import styles from './DecisionTracePage.module.css'
import { hasTechnicalDetails, integrityPresentation, orderedEntries, useDecisionTrace, type TraceEntry } from './decisionTrace'
import { TraceEntryDetails } from './TraceEntryDetails'

type View = 'operational' | 'technical'

const views: ReadonlyArray<{ key: View; label: string }> = [
  { key: 'operational', label: 'Operational' },
  { key: 'technical', label: 'Technical' },
]

function ViewToggle({ view, onChange }: { view: View; onChange: (view: View) => void }) {
  return (
    <div role="group" aria-label="View" className={styles.toggle}>
      <span className={styles.toggleLabel} aria-hidden="true">
        View:
      </span>
      {views.map((option) => (
        <button
          key={option.key}
          type="button"
          className={cx(styles.toggleButton, view === option.key && styles.toggleCurrent)}
          aria-pressed={view === option.key}
          onClick={() => onChange(option.key)}
        >
          {option.label}
        </button>
      ))}
    </div>
  )
}

interface EntryProps {
  entry: TraceEntry
  technical: boolean
  expanded: boolean
  onToggle: () => void
}

function TraceEntryItem({ entry, technical, expanded, onToggle }: EntryProps) {
  const actor = trailActorTone(entry)
  const label = trailStepLabel(entry.step)
  const detailsId = `trace-entry-${entry.seq}`
  const expandable = hasTechnicalDetails(entry)
  const showDetails = expandable && (technical || expanded)

  return (
    <TimelineItem tone={trailDotTone(entry)}>
      <article aria-label={`${entry.seq}. ${label}`} className={styles.entry}>
        <div className={styles.step}>
          <h3 className={styles.stepTitle}>
            {entry.seq}. {label}
          </h3>
          <ActorBadge actor={actor} name={entry.actor === 'system' ? undefined : entry.actor} />
        </div>
        <p className={styles.summary}>{entry.summary}</p>
        <time className={styles.time} dateTime={entry.occurredAt} title={formatDateTime(entry.occurredAt)}>
          {formatTraceTime(entry.occurredAt)}
        </time>
        {expandable && !technical && (
          <button type="button" className={styles.expand} aria-expanded={expanded} aria-controls={detailsId} onClick={onToggle}>
            {expanded ? 'Hide technical details' : 'Show technical details'}
          </button>
        )}
      </article>
      {showDetails && <TraceEntryDetails entry={entry} id={detailsId} />}
    </TimelineItem>
  )
}

export interface DecisionTracePageProps {
  claimId: string
  /** The claim reference for the subtitle. */
  reference: string
}

/**
 * The claim workspace's "Decision trace" tab (ui-design.md §6.3, FR-038 – FR-041): the append-only
 * trail as a vertical timeline with actor tones (§4.3), the hash-chain integrity badge, and an
 * operational/technical toggle; the technical view expands every entry's AI calls (tokens, cost,
 * latency), tool calls and RAG queries, the operational view one entry at a time.
 */
export function DecisionTracePage({ claimId, reference }: DecisionTracePageProps) {
  const trace = useDecisionTrace(claimId)
  const [search, setSearch] = useSearchParams()
  const [expanded, setExpanded] = useState<ReadonlySet<number>>(new Set())
  const view: View = search.get('view') === 'technical' ? 'technical' : 'operational'

  if (trace.isPending) return <LoadingState message="Loading decision trace…" />
  if (trace.isError) return <ProblemState problem={trace.error instanceof ApiProblem ? trace.error : {}} />

  const entries = orderedEntries(trace.data)
  const integrity = integrityPresentation(trace.data.integrity)

  const changeView = (next: View) =>
    setSearch(
      (current) => {
        const params = new URLSearchParams(current)
        if (next === 'technical') params.set('view', 'technical')
        else params.delete('view')
        return params
      },
      { replace: true },
    )

  const toggle = (seq: number) =>
    setExpanded((current) => {
      const next = new Set(current)
      if (!next.delete(seq)) next.add(seq)
      return next
    })

  return (
    <section aria-labelledby="decision-trace-title" className={styles.page}>
      <header className={styles.header}>
        <div className={styles.intro}>
          <h2 id="decision-trace-title" className={styles.title}>
            Decision trace
          </h2>
          <p className={styles.subtitle}>
            <span className={styles.mono}>{reference}</span> · {entries.length} {entries.length === 1 ? 'entry' : 'entries'} · append-only
          </p>
        </div>
        <div className={styles.controls}>
          <Badge tone={integrity.tone} dot>
            {integrity.label}
          </Badge>
          <ViewToggle view={view} onChange={changeView} />
        </div>
      </header>
      {integrity.tone === 'err' && (
        <Alert tone="err" title="Integrity check failed">
          The decision trail&apos;s hash chain does not verify: an entry may have been altered or removed. Treat this trail as
          unreliable and report it to the platform team.
        </Alert>
      )}
      {entries.length === 0 ? (
        <EmptyState message="No decision trail entries have been recorded for this claim yet." />
      ) : (
        <Timeline label="Decision trail">
          {entries.map((entry) => (
            <TraceEntryItem
              key={entry.seq}
              entry={entry}
              technical={view === 'technical'}
              expanded={expanded.has(entry.seq)}
              onToggle={() => toggle(entry.seq)}
            />
          ))}
        </Timeline>
      )}
    </section>
  )
}
