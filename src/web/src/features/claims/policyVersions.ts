import type { Schemas } from '../../shared/api/client'
import type { Labelled } from '../../shared/ui/tone'

export type PolicyVersionSummary = Schemas['PolicyVersionSummary']

export type PolicyVersionStatus = 'Active' | 'Superseded' | 'Scheduled'

const statuses: Record<PolicyVersionStatus, Labelled> = {
  Active: { label: 'Active', tone: 'ok' },
  Superseded: { label: 'Superseded', tone: 'system' },
  Scheduled: { label: 'Scheduled', tone: 'pending' },
}

/** Badge label and tone for a computed version status (ui-design.md §6.5). */
export const policyVersionStatusPresentation = (status: PolicyVersionStatus): Labelled => statuses[status]

const day = 86_400_000

/** Date-only `2026-07-01` as a UTC day number, so ranges compare without time zone shifts. */
function dayNumber(value: string): number {
  const [year = 0, month = 1, date = 1] = value.split('-').map(Number)
  return Date.UTC(year, month - 1, date) / day
}

/** Today's calendar date as `yyyy-mm-dd` in local time. */
export function isoToday(now: Date = new Date()): string {
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`
}

/** Status from today: in force (inclusive bounds) → Active, ended → Superseded, not yet started → Scheduled. */
export function policyVersionStatus(version: Pick<PolicyVersionSummary, 'effectiveFrom' | 'effectiveTo'>, today: string): PolicyVersionStatus {
  if (version.effectiveFrom > today) return 'Scheduled'
  if (version.effectiveTo !== null && version.effectiveTo < today) return 'Superseded'
  return 'Active'
}

export interface PolicyGroup {
  policyCode: string
  title: string
  versions: PolicyVersionSummary[]
}

/** Versions grouped by policy code (in first-seen order), each group in version order. */
export function groupByPolicy(versions: readonly PolicyVersionSummary[]): PolicyGroup[] {
  const groups = new Map<string, PolicyGroup>()
  for (const version of versions) {
    const group = groups.get(version.policyCode) ?? { policyCode: version.policyCode, title: version.title, versions: [] }
    group.versions.push(version)
    groups.set(version.policyCode, group)
  }
  for (const group of groups.values()) group.versions.sort((a, b) => a.version - b.version)
  return [...groups.values()]
}

export interface TimelineSegment {
  version: PolicyVersionSummary
  status: PolicyVersionStatus
  /** Offset and width as percentages of the timeline. */
  start: number
  width: number
  /** True when the version has no end date and is drawn to the horizon. */
  openEnded: boolean
}

export interface Timeline {
  from: string
  to: string
  segments: TimelineSegment[]
}

/** Open-ended versions run to today + 6 months on the timeline (ui-design.md §6.5). */
export function timelineHorizon(today: string): string {
  const [year = 0, month = 1, date = 1] = today.split('-').map(Number)
  const horizon = new Date(Date.UTC(year, month - 1 + 6, date))
  return horizon.toISOString().slice(0, 10)
}

/** Applicability timeline: one segment per version, sized in proportion to its date range. */
export function buildTimeline(versions: readonly PolicyVersionSummary[], today: string): Timeline | null {
  if (versions.length === 0) return null
  const horizon = timelineHorizon(today)
  const endOf = (v: PolicyVersionSummary) => {
    const end = v.effectiveTo ?? horizon
    // A scheduled open-ended version that starts after the horizon still gets a visible slice.
    return end < v.effectiveFrom ? v.effectiveFrom : end
  }
  const from = versions.map((v) => v.effectiveFrom).reduce((a, b) => (a < b ? a : b))
  const to = versions.map(endOf).reduce((a, b) => (a > b ? a : b))
  // Inclusive day counts: a version effective for one day still has width.
  const total = dayNumber(to) - dayNumber(from) + 1
  return {
    from,
    to,
    segments: versions.map((version) => ({
      version,
      status: policyVersionStatus(version, today),
      start: ((dayNumber(version.effectiveFrom) - dayNumber(from)) / total) * 100,
      width: ((dayNumber(endOf(version)) - dayNumber(version.effectiveFrom) + 1) / total) * 100,
      openEnded: version.effectiveTo === null,
    })),
  }
}
