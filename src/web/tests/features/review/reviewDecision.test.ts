import { describe, expect, it } from 'vitest'
import {
  activeRemedyPreview,
  buildRequestedItems,
  claimantMessagePrefill,
  decidingAiDecision,
  initialRemedy,
  offersProtectionPlanCheck,
  overridesAi,
  remedyOptions,
  remedyPendingReasonLabel,
  requiresJustification,
  requiresRemedyChoice,
} from '../../../src/features/review/reviewDecision'
import { aiExplanation, claimWithProtectionPlan, claimWithRemedyPreview, reviewClaim } from '../../support/reviewClaims'

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

  it("offers only the preview's allowed remedies and pre-selects the recommendation", () => {
    expect(remedyOptions(reviewClaim())).toEqual([])
    expect(remedyOptions(claimWithRemedyPreview())).toEqual(['Replacement', 'Refund'])
    expect(initialRemedy(claimWithRemedyPreview())).toBeNull()
    expect(
      initialRemedy(claimWithRemedyPreview({ scenario: 'CoveredDefect', allowedRemedies: ['Repair'], recommendedRemedy: 'Repair', needsChoice: false })),
    ).toBe('Repair')
    expect(initialRemedy(claimWithRemedyPreview({ allowedRemedies: ['Refund'] }))).toBe('Refund')
    expect(initialRemedy(claimWithRemedyPreview({ recommendedRemedy: 'Repair' }))).toBeNull()
  })

  it('requires a remedy choice only when approving with more than one allowed remedy', () => {
    const choice = claimWithRemedyPreview()
    expect(requiresRemedyChoice(choice, 'Approve')).toBe(true)
    expect(requiresRemedyChoice(choice, 'Reject')).toBe(false)
    expect(requiresRemedyChoice(choice, 'RequestInformation')).toBe(false)
    expect(requiresRemedyChoice(claimWithRemedyPreview({ allowedRemedies: ['Repair'] }), 'Approve')).toBe(false)
    expect(requiresRemedyChoice(claimWithRemedyPreview({ allowedRemedies: [] }), 'Approve')).toBe(false)
    expect(requiresRemedyChoice(reviewClaim(), 'Approve')).toBe(false)
  })

  it('labels the reason a remedy waits for a reviewer', () => {
    expect(remedyPendingReasonLabel('POLICY_CHOICE')).toBe('The policy allows more than one remedy')
    expect(remedyPendingReasonLabel('NO_REMEDY_RULE')).toBe('No remedy rule applies')
    expect(remedyPendingReasonLabel('REPAIR_FAILED_LIMIT')).toBe('Repair attempts limit reached')
    expect(remedyPendingReasonLabel('REPAIR_NOT_ECONOMICAL')).toBe('Repair is not economical')
    expect(remedyPendingReasonLabel(undefined)).toBe('Awaiting remedy decision')
  })
})

describe('protection plan check (002 FR-036)', () => {
  it('is offered only when approving a claim that states a plan', () => {
    expect(offersProtectionPlanCheck(claimWithProtectionPlan(), 'Approve')).toBe(true)
    expect(offersProtectionPlanCheck(claimWithProtectionPlan(), 'Reject')).toBe(false)
    expect(offersProtectionPlanCheck(claimWithProtectionPlan(), 'RequestInformation')).toBe(false)
    expect(offersProtectionPlanCheck(claimWithRemedyPreview(), 'Approve')).toBe(false)
  })

  it("switches to the plan preview's remedies once the plan is confirmed", () => {
    const claim = claimWithProtectionPlan()
    expect(remedyOptions(claim)).toEqual(['Repair'])
    expect(initialRemedy(claim)).toBe('Repair')
    expect(requiresRemedyChoice(claim, 'Approve')).toBe(false)
    expect(activeRemedyPreview(claim, true)?.scenario).toBe('ProtectionPlanDamage')
    expect(remedyOptions(claim, true)).toEqual(['Replacement', 'Refund'])
    expect(initialRemedy(claim, true)).toBeNull()
    expect(requiresRemedyChoice(claim, 'Approve', true)).toBe(true)
  })

  it('keeps the remedy preview when the policy has no plan preview', () => {
    const claim = claimWithProtectionPlan({ withPlanPreview: false })
    expect(remedyOptions(claim, true)).toEqual(['Repair'])
    expect(initialRemedy(claim, true)).toBe('Repair')
  })
})