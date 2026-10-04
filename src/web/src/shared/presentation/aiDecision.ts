import type { Actor, Labelled } from '../ui/tone'

export type AiDecision = 'APPROVE' | 'REJECT' | 'REQUEST_MORE_INFORMATION' | 'HUMAN_REVIEW'
export type Coverage = 'COVERED' | 'NOT_COVERED' | 'UNDETERMINED'
export type RiskLevel = 'Low' | 'Medium' | 'High'
export type RiskSignalSource = 'AI' | 'Deterministic'

const aiDecisions: Record<AiDecision, Labelled> = {
  APPROVE: { label: 'Approve', tone: 'ok' },
  REJECT: { label: 'Reject', tone: 'err' },
  REQUEST_MORE_INFORMATION: { label: 'Request information', tone: 'warn' },
  HUMAN_REVIEW: { label: 'Human review', tone: 'human' },
}

const coverages: Record<Coverage, string> = {
  COVERED: 'Covered',
  NOT_COVERED: 'Not covered',
  UNDETERMINED: 'Undetermined',
}

const riskColors: Record<RiskLevel, string> = {
  Low: 'var(--color-success)',
  Medium: 'var(--color-warning)',
  High: 'var(--color-error)',
}

/**
 * The AI's recommendation word and the tone of that word (ui-design.md §4.2). Render it only inside
 * an AI panel or next to an `AI` badge so the outcome colour never reads as a final decision.
 */
export function aiDecisionPresentation(decision: AiDecision): Labelled {
  return aiDecisions[decision] ?? { label: decision, tone: 'pending' }
}

/** "Policy match" label for `Recommendation.coverage`. */
export function coverageLabel(coverage: Coverage): string {
  return coverages[coverage] ?? coverage
}

/** Dot and bar colour for a risk level; always shown together with the level text. */
export function riskLevelColor(level: RiskLevel): string {
  return riskColors[level] ?? 'var(--color-system)'
}

/** Guardrail `CheckResult`: ✓ in `ok` when passed, ✕ in `err` when failed. */
export function checkResultPresentation(passed: boolean): Labelled & { symbol: string } {
  return passed
    ? { label: 'Passed', tone: 'ok', symbol: '✓' }
    : { label: 'Failed', tone: 'err', symbol: '✕' }
}

/** Evidence consistency: does the evidence agree with what the claim says? */
export function consistencyPresentation(match: boolean): Labelled {
  return match ? { label: 'Supports', tone: 'ok' } : { label: 'Conflicts', tone: 'err' }
}

/** Risk signals from the model carry an `AI` badge; deterministic rules carry `SYSTEM`. */
export function riskSignalSourceActor(source: RiskSignalSource): Actor {
  return source === 'AI' ? 'ai' : 'system'
}
