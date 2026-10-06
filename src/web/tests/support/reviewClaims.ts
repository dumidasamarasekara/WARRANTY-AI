import type { ClaimDetail, ReviewQueueItem } from '../../src/features/review/reviewDecision'

export const claimId = '7a1d2c3e-4b5f-4a6b-8c7d-9e0f1a2b3c4d'
export const aiExplanation = 'Your repair is covered under the manufacturer warranty and has been approved.'

type Overrides = {
  decision?: 'APPROVE' | 'REJECT' | 'REQUEST_MORE_INFORMATION' | 'HUMAN_REVIEW'
  isValid?: boolean
  textSafe?: boolean | null
  claimantExplanation?: string
}

/** A claim in `UnderReview` with an AI recommendation; `textSafe: null` leaves out the `CLAIMANT_TEXT_SAFE` check. */
export function reviewClaim({ decision = 'APPROVE', isValid = true, textSafe = true, claimantExplanation = aiExplanation }: Overrides = {}): ClaimDetail {
  return {
    claimId,
    reference: 'K7M2Q9X4TB',
    status: 'UnderReview',
    channel: 'ClaimantPortal',
    claimDate: '2026-10-01',
    region: 'NA',
    customer: { country: 'US' },
    product: { modelCode: 'AUR-BOOK15', serialNumber: 'SN-1', name: 'Aurora Book 15', inCatalog: true, claimValue: 1400 },
    purchase: { date: '2026-03-01', place: 'Aurora Store', price: 1400, currency: 'USD' },
    problemDescription: 'The screen flickers.',
    evidence: [{ evidenceId: '11111111-1111-4111-8111-111111111111', ref: 'EV-1', kind: 'Invoice', fileName: 'invoice.pdf', contentType: 'application/pdf', round: 1 }],
    latestEvaluation: {
      runId: '22222222-2222-4222-8222-222222222222',
      round: 1,
      status: 'Completed',
      validation: [],
      evidenceFindings: [],
      policyReferences: [
        {
          ref: 'POL-1',
          clauseKey: 'AUR-WP-2.1',
          clauseType: 'Coverage',
          documentTitle: 'Aurora Limited Warranty',
          version: 2,
          effectiveFrom: '2026-01-01',
          effectiveTo: null,
          cited: true,
        },
      ],
      risk: {
        score: 30,
        level: 'Medium',
        stage: 'Full',
        signals: [{ code: 'HIGH_VALUE', source: 'Deterministic', severity: 'Medium', detail: 'Above the auto-approval limit' }],
      },
      recommendation: {
        isValid,
        validationErrors: [],
        decision,
        coverage: 'COVERED',
        confidence: 91,
        reasoningSummary: 'Defect within the warranty period.',
        claimantExplanation,
        evidenceRefs: [],
        policyRefs: [],
        missingInformation: [],
        model: 'claude-decision',
        promptVersion: 'decision@1',
      },
      guardrails: {
        disposition: 'HumanReview',
        reasons: ['VALUE_ABOVE_LIMIT'],
        checks: textSafe === null ? [] : [{ code: 'CLAIMANT_TEXT_SAFE', passed: textSafe }],
      },
    },
    reviewDecisions: [],
    requestedItems: [],
    autoInfoRequestCount: 0,
    reviewerInfoRequested: false,
  }
}

export function queueItem(overrides: Partial<ReviewQueueItem> = {}): ReviewQueueItem {
  return {
    claimId,
    reference: 'K7M2Q9X4TB',
    productName: 'Aurora Book 15',
    claimValue: 1400,
    aiDecision: 'APPROVE',
    confidence: 91,
    riskLevel: 'Medium',
    escalatedAt: '2026-10-06T08:00:00Z',
    submittedByMe: false,
    escalationReasons: ['Claim value above the automatic approval limit'],
    ...overrides,
  }
}
