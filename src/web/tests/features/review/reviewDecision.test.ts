import { describe, expect, it } from 'vitest'
import {
  buildRequestedItems,
  claimantMessagePrefill,
  decidingAiDecision,
  overridesAi,
  requiresJustification,
} from '../../../src/features/review/reviewDecision'
import { aiExplanation, reviewClaim } from '../../support/reviewClaims'

describe('review decision rules', () => {
  it('treats only a valid APPROVE or REJECT as an AI decision', () => {
    expect(decidingAiDecision(reviewClaim({ decision: 'APPROVE' }))).toBe('APPROVE')
    expect(decidingAiDecision(reviewClaim({ decision: 'REJECT' }))).toBe('REJECT')
    expect(decidingAiDecision(reviewClaim({ decision: 'APPROVE', isValid: false }))).toBeNull()
    expect(decidingAiDecision(reviewClaim({ decision: 'HUMAN_REVIEW' }))).toBeNull()
    expect(decidingAiDecision(reviewClaim({ decision: 'REQUEST_MORE_INFORMATION' }))).toBeNull()
    expect(decidingAiDecision({ ...reviewClaim(), latestEvaluation: undefined })).toBeNull()
  })

  it('counts a decision as an override only against a valid APPROVE or REJECT', () => {
    const approve = reviewClaim({ decision: 'APPROVE' })
    expect(overridesAi(approve, 'Approve')).toBe(false)
    expect(overridesAi(approve, 'Reject')).toBe(true)
    expect(overridesAi(approve, 'RequestInformation')).toBe(true)
    for (const claim of [reviewClaim({ decision: 'HUMAN_REVIEW' }), reviewClaim({ isValid: false })]) {
      expect(overridesAi(claim, 'Approve')).toBe(false)
      expect(overridesAi(claim, 'Reject')).toBe(false)
    }
  })

  it('requires the internal justification when overriding or rejecting', () => {
    const approve = reviewClaim({ decision: 'APPROVE' })
    expect(requiresJustification(approve, 'Approve')).toBe(false)
    expect(requiresJustification(approve, 'Reject')).toBe(true)
    expect(requiresJustification(reviewClaim({ decision: 'REJECT' }), 'Reject')).toBe(true)
    expect(requiresJustification(reviewClaim({ decision: 'REJECT' }), 'Approve')).toBe(true)
    expect(requiresJustification(reviewClaim({ decision: 'HUMAN_REVIEW' }), 'Approve')).toBe(false)
    expect(requiresJustification(reviewClaim({ decision: 'HUMAN_REVIEW' }), 'RequestInformation')).toBe(false)
  })

  it('pre-fills the claimant message only for a matching, valid and CLAIMANT_TEXT_SAFE AI explanation', () => {
    expect(claimantMessagePrefill(reviewClaim({ decision: 'APPROVE' }), 'Approve')).toBe(aiExplanation)
    expect(claimantMessagePrefill(reviewClaim({ decision: 'APPROVE' }), 'Reject')).toBe('')
    expect(claimantMessagePrefill(reviewClaim({ decision: 'APPROVE', isValid: false }), 'Approve')).toBe('')
    expect(claimantMessagePrefill(reviewClaim({ decision: 'APPROVE', textSafe: false }), 'Approve')).toBe('')
    expect(claimantMessagePrefill(reviewClaim({ decision: 'APPROVE', textSafe: null }), 'Approve')).toBe('')
    expect(claimantMessagePrefill(reviewClaim({ decision: 'HUMAN_REVIEW' }), 'Approve')).toBe('')
  })

  it('sends the preset reason per requested item and the reviewer wording for OTHER', () => {
    expect(buildRequestedItems(['PHOTO_OF_SERIAL_LABEL', 'OTHER'], '  The original packaging  ')).toEqual([
      { item: 'PHOTO_OF_SERIAL_LABEL', reason: 'Please upload a photo of the label showing the serial number.' },
      { item: 'OTHER', reason: 'The original packaging' },
    ])
  })
})
