import { cx } from '../cx'
import styles from './ConfidenceMeter.module.css'

export interface ConfidenceMeterProps {
  /** 0–100; values outside the range are clamped. */
  value: number
  /** `sm`: 56 px bar beside the number; `md`: metric number over an 8 px bar. */
  size?: 'sm' | 'md'
  label?: string
  className?: string
}

/**
 * AI confidence as a number plus a violet bar. No colour bands: the minimum confidence is a
 * per-tenant setting, so the bar never implies pass or fail.
 */
export function ConfidenceMeter({ value, size = 'sm', label = 'Confidence', className }: ConfidenceMeterProps) {
  const percent = Math.round(Math.min(100, Math.max(0, value)))
  return (
    <div
      role="meter"
      aria-label={label}
      aria-valuenow={percent}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuetext={`${percent}%`}
      className={cx(styles.meter, styles[size], className)}
    >
      <span className={styles.value}>{percent}%</span>
      <span className={styles.track}>
        <span className={styles.fill} style={{ width: `${percent}%` }} />
      </span>
    </div>
  )
}
