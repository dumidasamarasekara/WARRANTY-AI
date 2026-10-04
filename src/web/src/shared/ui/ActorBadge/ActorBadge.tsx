import { cx } from '../cx'
import type { Actor } from '../tone'
import tones from '../tone.module.css'
import styles from './ActorBadge.module.css'

const actorLabels: Record<Actor, string> = { ai: 'AI', human: 'HUMAN', system: 'SYSTEM' }

export interface ActorBadgeProps {
  actor: Actor
  /** Agent, reviewer or service name shown after the tag. */
  name?: string
  className?: string
}

/** Violet AI recommends, blue a person decides, grey the system executes. */
export function ActorBadge({ actor, name, className }: ActorBadgeProps) {
  return (
    <span className={cx(styles.actorBadge, tones[actor], className)}>
      <span className={styles.tag}>{actorLabels[actor]}</span>
      {name && <span className={styles.name}>{name}</span>}
    </span>
  )
}
