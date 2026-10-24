import { repairStatusPresentation } from '../../shared/presentation'
import { ActorBadge, Card, Timeline, TimelineItem } from '../../shared/ui'
import type { RepairEvent } from './remedyQueries'
import { repairEventLabel, repairEventOrigin } from './remedyView'
import styles from './RemedyTab.module.css'

function RepairEventEntry({ event }: { event: RepairEvent }) {
  const change =
    event.fromStatus && event.toStatus && event.fromStatus !== event.toStatus
      ? `${repairStatusPresentation(event.fromStatus).label} → ${repairStatusPresentation(event.toStatus).label}`
      : undefined
  return (
    <TimelineItem tone="human">
      <p className={styles.historyHead}>
        <ActorBadge actor="human" name={event.actor} />
        <strong>{repairEventLabel(event.type)}</strong>
      </p>
      <p className={styles.muted}>{repairEventOrigin(event)}</p>
      {change && <p className={styles.muted}>{change}</p>}
      {event.note && <p>{event.note}</p>}
    </TimelineItem>
  )
}

/**
 * Right column of the Remedy tab (ui-screens.md §2): the repair events, oldest first, each with its
 * type, reported and recorded times, source, actor and note. Updates are reported by the repair centre
 * and recorded by staff, so every entry is blue human ink.
 */
export function RepairEventsTimeline({ events }: { events: readonly RepairEvent[] }) {
  return (
    <Card title="Repair events" titleAs="h2">
      {events.length === 0 ? (
        <p className={styles.muted}>No repair updates recorded yet.</p>
      ) : (
        <Timeline label="Repair events">
          {[...events]
            .sort((a, b) => a.seq - b.seq)
            .map((event) => (
              <RepairEventEntry key={event.seq} event={event} />
            ))}
        </Timeline>
      )}
    </Card>
  )
}
