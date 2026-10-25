import { replacementStatusPresentation } from '../../shared/presentation'
import { ActorBadge, Card, Timeline, TimelineItem } from '../../shared/ui'
import type { ReplacementEvent } from './remedyQueries'
import { correctedBy } from './remedyView'
import { isSystemEvent, replacementEventAnchor, replacementEventLabel, replacementEventName, replacementEventOrigin } from './replacementView'
import styles from './RemedyTab.module.css'

interface ReplacementEventEntryProps {
  event: ReplacementEvent
  /** The entry this correction corrects, when it is a correction. */
  corrects?: ReplacementEvent
  /** Seq of the correction that corrected this entry, when it was corrected. */
  correctedBySeq?: number
}

function ReplacementEventEntry({ event, corrects, correctedBySeq }: ReplacementEventEntryProps) {
  const actor = isSystemEvent(event) ? 'system' : 'human'
  const change =
    event.fromStatus && event.toStatus && event.fromStatus !== event.toStatus
      ? `${replacementStatusPresentation(event.fromStatus).label} → ${replacementStatusPresentation(event.toStatus).label}`
      : undefined
  const corrected = correctedBySeq !== undefined
  return (
    <TimelineItem tone={actor}>
      <div id={replacementEventAnchor(event.seq)} className={styles.eventEntry}>
        <p className={styles.historyHead}>
          <ActorBadge actor={actor} name={actor === 'human' ? event.actor : undefined} />
          <strong className={corrected ? styles.corrected : undefined}>{replacementEventLabel(event.type)}</strong>
          <span className={styles.time}>Entry {event.seq}</span>
        </p>
        {/* A corrected entry stays visible, struck through (ui-screens.md §2). */}
        <div className={corrected ? styles.corrected : undefined}>
          <p className={styles.muted}>{replacementEventOrigin(event)}</p>
          {change && <p className={styles.muted}>{change}</p>}
          {event.note && <p>{event.note}</p>}
        </div>
        {corrected && (
          <p className={styles.muted}>
            Corrected by <a href={`#${replacementEventAnchor(correctedBySeq)}`}>entry {correctedBySeq}</a>
          </p>
        )}
        {event.correctsSeq !== undefined && (
          <p className={styles.muted}>
            Corrects{' '}
            <a href={`#${replacementEventAnchor(event.correctsSeq)}`}>
              {corrects ? replacementEventName(corrects) : `entry ${event.correctsSeq}`}
            </a>
          </p>
        )}
      </div>
    </TimelineItem>
  )
}

/**
 * Right column of the Remedy tab for a replacement (ui-screens.md §2): the replacement events, oldest first, each with
 * its type, reported and recorded times, source, actor and note. Updates reported by a location and recorded by staff
 * are blue human ink; the suggested location and a cancellation are grey system entries. A correction links to the
 * entry it corrects, and the corrected entry is struck through, never hidden.
 */
export function ReplacementEventsTimeline({ events }: { events: readonly ReplacementEvent[] }) {
  const sorted = [...events].sort((a, b) => a.seq - b.seq)
  const corrections = correctedBy(sorted)
  const bySeq = new Map(sorted.map((event) => [event.seq, event]))
  return (
    <Card title="Replacement events" titleAs="h2">
      {sorted.length === 0 ? (
        <p className={styles.muted}>No replacement updates recorded yet.</p>
      ) : (
        <Timeline label="Replacement events">
          {sorted.map((event) => (
            <ReplacementEventEntry
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
