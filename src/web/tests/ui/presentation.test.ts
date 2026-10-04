import { describe, expect, it } from 'vitest'
import { tenantInitials, tenantMarkerColor } from '../../src/app/tenantTheme'
import {
  aiDecisionPresentation,
  checkResultPresentation,
  claimStatusPresentation,
  consistencyPresentation,
  coverageLabel,
  decidedByPresentation,
  dispositionPresentation,
  formatDate,
  formatDateTime,
  formatFileSize,
  formatMoney,
  formatRelative,
  formatTime,
  formatTraceTime,
  riskLevelColor,
  riskSignalSourceActor,
  trailActorTone,
  trailDotTone,
  trailStepLabel,
  type AiDecision,
  type ClaimStatus,
  type Disposition,
} from '../../src/shared/presentation'

describe('claim status (§4.1)', () => {
  it.each<[ClaimStatus, string, string]>([
    ['Submitted', 'Submitted', 'pending'],
    ['UnderEvaluation', 'Under evaluation', 'pending'],
    ['PendingInformation', 'Pending information', 'warn'],
    ['UnderReview', 'Under review', 'human'],
    ['Approved', 'Approved', 'ok'],
    ['Rejected', 'Rejected', 'err'],
  ])('%s → %s (%s)', (status, label, tone) => {
    expect(claimStatusPresentation(status)).toEqual({ label, tone })
  })

  it('labels who decided', () => {
    expect(decidedByPresentation('System')).toEqual({ label: 'Decided by system', tone: 'system' })
    expect(decidedByPresentation('Reviewer')).toEqual({ label: 'Decided by reviewer', tone: 'human' })
  })
})

describe('AI and guardrail values (§4.2)', () => {
  it.each<[AiDecision, string, string]>([
    ['APPROVE', 'Approve', 'ok'],
    ['REJECT', 'Reject', 'err'],
    ['REQUEST_MORE_INFORMATION', 'Request information', 'warn'],
    ['HUMAN_REVIEW', 'Human review', 'human'],
  ])('AI decision %s → %s (%s)', (decision, label, tone) => {
    expect(aiDecisionPresentation(decision)).toEqual({ label, tone })
  })

  it.each<[Disposition, string, string]>([
    ['AutoApprove', 'Auto-approved', 'system'],
    ['AutoReject', 'Auto-rejected', 'system'],
    ['RequestInformation', 'Information requested', 'warn'],
    ['HumanReview', 'Escalated to a person', 'human'],
  ])('disposition %s → %s (%s)', (disposition, label, tone) => {
    expect(dispositionPresentation(disposition)).toEqual({ label, tone })
  })

  it('labels the policy match', () => {
    expect(coverageLabel('COVERED')).toBe('Covered')
    expect(coverageLabel('NOT_COVERED')).toBe('Not covered')
    expect(coverageLabel('UNDETERMINED')).toBe('Undetermined')
  })

  it('colours risk levels with status tokens', () => {
    expect(riskLevelColor('Low')).toBe('var(--color-success)')
    expect(riskLevelColor('Medium')).toBe('var(--color-warning)')
    expect(riskLevelColor('High')).toBe('var(--color-error)')
  })

  it('maps check results, consistency and risk signal sources', () => {
    expect(checkResultPresentation(true)).toEqual({ label: 'Passed', tone: 'ok', symbol: '✓' })
    expect(checkResultPresentation(false)).toEqual({ label: 'Failed', tone: 'err', symbol: '✕' })
    expect(consistencyPresentation(true)).toEqual({ label: 'Supports', tone: 'ok' })
    expect(consistencyPresentation(false)).toEqual({ label: 'Conflicts', tone: 'err' })
    expect(riskSignalSourceActor('AI')).toBe('ai')
    expect(riskSignalSourceActor('Deterministic')).toBe('system')
  })
})

describe('decision trail entries (§4.3)', () => {
  it('derives the actor tone', () => {
    expect(trailActorTone({ step: 'ClaimSubmitted', actor: 'system' })).toBe('system')
    for (const agent of ['intake', 'evidence', 'policy', 'decision']) {
      expect(trailActorTone({ step: 'AiRecommended', actor: agent })).toBe('ai')
    }
    expect(trailActorTone({ step: 'CoverageAssessed', actor: 'adjudication-service', aiCalls: [{}] })).toBe('ai')
    expect(trailActorTone({ step: 'ReviewerDecided', actor: '7c1e5d2a-staff-sub' })).toBe('human')
  })

  it('overrides the dot for failed AI steps and escalations', () => {
    expect(trailDotTone({ step: 'AiStepFailed', actor: 'decision' })).toBe('err')
    expect(trailDotTone({ step: 'EscalatedToReview', actor: 'system' })).toBe('human')
    expect(trailDotTone({ step: 'GuardrailsEvaluated', actor: 'system' })).toBe('system')
    expect(trailDotTone({ step: 'AiRecommended', actor: 'decision' })).toBe('ai')
  })

  it('labels steps and passes unknown steps through', () => {
    expect(trailStepLabel('ClaimExtracted')).toBe('Claim details extracted')
    expect(trailStepLabel('EvidenceAnalyzed')).toBe('Evidence analysed')
    expect(trailStepLabel('EscalatedToReview')).toBe('Escalated to human review')
    expect(trailStepLabel('AiRecommended')).toBe('AI recommendation')
    expect(trailStepLabel('SomethingNew')).toBe('SomethingNew')
  })
})

describe('formatting (§4.4)', () => {
  const instant = new Date(2026, 9, 2, 9, 41, 7)

  it('formats dates and times', () => {
    expect(formatDate(instant)).toBe('02 Oct 2026')
    expect(formatTime(instant)).toBe('09:41')
    expect(formatDateTime(instant)).toBe('02 Oct 2026, 09:41')
    expect(formatTraceTime(instant)).toBe('09:41:07')
    expect(formatDate(new Date(2026, 8, 5))).toBe('05 Sep 2026')
  })

  it('reads date-only values as calendar dates', () => {
    expect(formatDate('2026-10-02')).toBe('02 Oct 2026')
    expect(formatDate('2025-01-31')).toBe('31 Jan 2025')
  })

  it('formats relative times for queues', () => {
    const at = (minutesAgo: number) => new Date(instant.getTime() - minutesAgo * 60_000)
    expect(formatRelative(at(0), instant)).toBe('just now')
    expect(formatRelative(at(5), instant)).toBe('5 min ago')
    expect(formatRelative(at(125), instant)).toBe('2 h ago')
    expect(formatRelative(at(3 * 24 * 60), instant)).toBe('3 d ago')
  })

  it('formats money with the currency code', () => {
    expect(formatMoney(1400, 'USD')).toMatch(/^USD\s1,400\.00$/)
    expect(formatMoney(89.5, 'EUR')).toMatch(/^EUR\s89\.50$/)
  })

  it('formats file sizes', () => {
    expect(formatFileSize(512)).toBe('512 B')
    expect(formatFileSize(2048)).toBe('2 KB')
    expect(formatFileSize(2.4 * 1024 * 1024)).toBe('2.4 MB')
  })
})

describe('tenant theme', () => {
  it('maps the seeded tenants to their marker tokens', () => {
    expect(tenantMarkerColor('Aurora Electronics')).toBe('var(--color-tenant-aurora)')
    expect(tenantMarkerColor('Borealis Devices')).toBe('var(--color-tenant-borealis)')
  })

  it('falls back to the neutral marker', () => {
    expect(tenantMarkerColor('Someone Else')).toBe('var(--color-tenant-unknown)')
    expect(tenantMarkerColor(undefined)).toBe('var(--color-tenant-unknown)')
    expect(tenantMarkerColor('')).toBe('var(--color-tenant-unknown)')
  })

  it('derives tile initials from the display name', () => {
    expect(tenantInitials('Aurora Electronics')).toBe('AE')
    expect(tenantInitials('borealis')).toBe('B')
    expect(tenantInitials(null)).toBe('')
  })
})
