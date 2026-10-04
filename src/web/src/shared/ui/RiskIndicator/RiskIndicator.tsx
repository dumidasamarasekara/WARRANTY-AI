import type { CSSProperties } from 'react'
import { riskLevelColor, type RiskLevel } from '../../presentation/aiDecision'
import { cx } from '../cx'
import styles from './RiskIndicator.module.css'

export interface RiskIndicatorProps {
  level: RiskLevel
  /** 0–100; adds the number and a bar when present. */
  score?: number
  className?: string
}

/** Risk level as a coloured dot plus the level text (and score), never colour alone. */
export function RiskIndicator({ level, score, className }: RiskIndicatorProps) {
  const style = { '--risk-color': riskLevelColor(level) } as CSSProperties
  const percent = score === undefined ? undefined : Math.min(100, Math.max(0, score))
  return (
    <span className={cx(styles.risk, className)} style={style}>
      <span className={styles.dot} aria-hidden="true" />
      <span className={styles.text}>
        {level}
        {score !== undefined && ` · ${score}`}
      </span>
      {percent !== undefined && (
        <span className={styles.track} aria-hidden="true">
          <span className={styles.fill} style={{ width: `${percent}%` }} />
        </span>
      )}
    </span>
  )
}
