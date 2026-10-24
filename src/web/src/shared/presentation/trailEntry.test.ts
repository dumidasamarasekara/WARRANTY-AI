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

describe('trail step labels for the replacement fulfilment (003)', () => {
  it.each([
    ['ReplacementLocationAssigned', 'Replacement location assigned'],
    ['FaultyProductReturned', 'Faulty product returned'],
    ['ReplacementModelProposed', 'Replacement model proposed'],
    ['ReplacementModelDecided', 'Replacement model decided'],
    ['ReplacementStatusUpdated', 'Replacement status updated'],
    ['ReplacementUnitRegistered', 'Replacement unit registered'],
  ])('%s → %s', (step, label) => {
    expect(trailStepLabel(step)).toBe(label)
  })

  it('never says shipped, on its way or delivered: replacements are collected', () => {
    for (const step of ['ReplacementStatusUpdated', 'ReplacementUnitRegistered', 'FaultyProductReturned']) {
      expect(trailStepLabel(step)).not.toMatch(/shipped|on its way|delivered/i)
    }
  })
})
