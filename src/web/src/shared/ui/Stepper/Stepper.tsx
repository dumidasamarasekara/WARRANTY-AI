import type { ReactNode } from 'react'
import { cx } from '../cx'
import styles from './Stepper.module.css'

export type StepState = 'done' | 'current' | 'blocked' | 'upcoming'

export interface StepperStep {
  key: string
  label: ReactNode
  state: StepState
}

const stateText: Record<StepState, string> = {
  done: 'completed',
  current: 'current step',
  blocked: 'needs your attention',
  upcoming: 'not started',
}

export interface StepperProps {
  steps: readonly StepperStep[]
  label: string
  className?: string
}

/** Horizontal progress steps; vertical below 640 px. A blocked step is the current one. */
export function Stepper({ steps, label, className }: StepperProps) {
  return (
    <ol aria-label={label} className={cx(styles.stepper, className)}>
      {steps.map((step, index) => (
        <li
          key={step.key}
          className={cx(styles.step, styles[step.state])}
          aria-current={step.state === 'current' || step.state === 'blocked' ? 'step' : undefined}
        >
          <span className={styles.marker} aria-hidden="true">
            {step.state === 'done' ? '✓' : step.state === 'blocked' ? '!' : index + 1}
          </span>
          <span className={styles.label}>
            {step.label}
            <span className="visually-hidden"> ({stateText[step.state]})</span>
          </span>
        </li>
      ))}
    </ol>
  )
}
