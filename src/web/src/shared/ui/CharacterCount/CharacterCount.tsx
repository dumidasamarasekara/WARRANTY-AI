import { cx } from '../cx'
import styles from './CharacterCount.module.css'

export interface CharacterCountProps {
  length: number
  min?: number
  max: number
  className?: string
}

const format = (value: number) => value.toLocaleString('en-US')

/**
 * Visible count under a textarea. Only changes of state (below minimum, over maximum) are announced,
 * not every keystroke.
 */
export function CharacterCount({ length, min, max, className }: CharacterCountProps) {
  const state = length > max ? 'over' : min !== undefined && length < min ? 'below' : 'ok'
  const hint =
    state === 'over' ? `${format(length - max)} over the limit` : state === 'below' ? `${format(min! - length)} more needed` : null
  const announcement =
    state === 'over'
      ? `Over the maximum of ${format(max)} characters`
      : state === 'below'
        ? `Enter at least ${format(min!)} characters`
        : ''

  return (
    <div className={cx(styles.count, styles[state], className)}>
      <span>
        {format(length)} / {format(max)}
        {hint && ` · ${hint}`}
      </span>
      <span className="visually-hidden" aria-live="polite">
        {announcement}
      </span>
    </div>
  )
}
