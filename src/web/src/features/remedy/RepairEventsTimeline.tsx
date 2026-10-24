import { repairStatusPresentation } from '../../shared/presentation'
import { ActorBadge, Card, Timeline, TimelineItem } from '../../shared/ui'
import type { RepairEvent } from './remedyQueries'
import { correctedBy, repairEventAnchor, repairEventLabel, repairEventName, repairEventOrigin } from './remedyView'
import styles from './RemedyTab.module.css'

interface RepairEventEntryProps {
  event: RepairEvent
  /** The entry this correction corrects, when it is a correction. */
  corrects?: RepairEvent
  /** Seq of the correction that corrected this entry, when it was corrected. */
  correctedBySeq?: number
}

function RepairEventEntry({ event, corrects, correctedBySeq }: RepairEventEntryProps) {
  const change =
    event.fromStatus && event.toStatus && event.fromStatus !== event.toStatus
      ? `${repairStatusPresentation(event.fromStatus).label} → ${repairStatusPresentation(event.toStatus).label}`
      : undefined
  const corrected = correctedBySeq !== undefined
  return (
    <TimelineItem tone="human">
      <div id={repairEventAnchor(event.seq)} className={styles.eventEntry}>
        <p className={styles.historyHead}>
          <ActorBadge actor="human" name={event.actor} />
          <strong className={corrected ? styles.corrected : undefined}>{repairEventLabel(event.type)}</strong>
          <span className={styles.time}>Entry {event.seq}</span>
        </p>
        {/* A corrected entry stays visible, struck through (ui-screens.md §2). */}
        <div className={corrected ? styles.corrected : undefined}>
          <p className={styles.muted}>{repairEventOrigin(event)}</p>
          {change && <p className={styles.muted}>{change}</p>}
          {event.note && <p>{event.note}</p>}
        </div>
        {corrected && (
          <p className={styles.muted}>
            Corrected by <a href={`#${repairEventAnchor(correctedBySeq)}`}>entry {correctedBySeq}</a>
          </p>
        )}
        {event.correctsSeq !== undefined && (
          <p className={styles.muted}>
            Corrects{' '}
            <a href={`#${repairEventAnchor(event.correctsSeq)}`}>
              {corrects ? repairEventName(corrects) : `entry ${event.correctsSeq}`}
            </a>
          </p>
        )}
      </div>
    </TimelineItem>
  )
}

/**
 * Right column of the Remedy tab (ui-screens.md §2): the repair events, oldest first, each with its
 * type, reported and recorded times, source, actor and note. Updates are reported by the repair centre
 * and recorded by staff, so every entry is blue human ink. A correction links to the entry it corrects,
 * and the corrected entry is struck through, never hidden.
 */
export function RepairEventsTimeline({ events }: { events: readonly RepairEvent[] }) {
  const sorted = [...events].sort((a, b) => a.seq - b.seq)
  const corrections = correctedBy(sorted)
  const bySeq = new Map(sorted.map((event) => [event.seq, event]))
  return (
    <Card title="Repair events" titleAs="h2">
      {sorted.length === 0 ? (
        <p className={styles.muted}>No repair updates recorded yet.</p>
      ) : (
        <Timeline label="Repair events">
          {sorted.map((event) => (
            <RepairEventEntry
              key={event.seq}
              event={event}
              corrects={event.correctsSeq !== undefined ? bySeq.get(event.correctsSeq) : undefined}
              correctedBySeq={corrections.get(event.seq)}
            />
          ))}
        </Timeline>
      )}
    </Card>
  )
}
