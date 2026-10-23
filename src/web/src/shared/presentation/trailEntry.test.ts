import { describe, expect, it } from 'vitest'
import { trailStepLabel } from './trailEntry'

describe('trail step labels for remedies and the repair hand-over (002)', () => {
  it.each([
    ['RemedyRecommended', 'Remedy from policy'],
    ['RemedyDecided', 'Remedy decided'],
    ['RepairCentreAssigned', 'Repair centre assigned'],
    ['RepairHandedOver', 'Handed over to repair centre'],
    ['RepairStatusUpdated', 'Repair status updated'],
    ['RepairAttemptEnded', 'Repair attempt ended'],
    ['RemedyCompleted', 'Remedy completed'],
  ])('%s → %s', (step, label) => {
    expect(trailStepLabel(step)).toBe(label)
  })

  it('never calls the policy remedy an AI recommendation', () => {
    expect(trailStepLabel('RemedyRecommended')).not.toMatch(/\bAI\b|recommend/i)
  })
})
