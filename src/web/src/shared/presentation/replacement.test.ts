import { describe, expect, expectTypeOf, it } from 'vitest'
import type { Schemas } from '../api/client'
import {
  pendingModelProposalPresentation,
  replacementProductRulePresentation,
  replacementReturnRulePresentation,
  replacementStatusPresentation,
  returnStatePresentation,
  type ReplacementProductRule,
  type ReplacementReturnRule,
  type ReplacementStatus,
  type ReturnState,
} from './replacement'

const statuses: ReplacementStatus[] = [
  'AwaitingReturn',
  'AwaitingUnit',
  'ReadyForCollection',
  'HandedToCustomer',
  'NotAvailable',
  'Cancelled',
]
const returnStates: ReturnState[] = ['NotYetReturned', 'Returned', 'HeldByRepairCentre', 'Mismatch', 'Accepted']

describe('replacement values (003 ui-screens.md §1)', () => {
  it('declares exactly the values of the REST contract', () => {
    // Checked by `tsc -b` in the build: a value added to or removed from the contract fails it.
    expectTypeOf<ReplacementStatus>().toEqualTypeOf<Schemas['ReplacementStatus']>()
    expectTypeOf<ReturnState>().toEqualTypeOf<Schemas['ReturnState']>()
    expectTypeOf<ReplacementProductRule>().toEqualTypeOf<Schemas['ReplacementView']['productRule']>()
    expectTypeOf<ReplacementReturnRule>().toEqualTypeOf<Schemas['ReplacementView']['returnRule']>()
  })

  it.each<[ReplacementStatus, string, string]>([
    ['AwaitingReturn', 'Awaiting return', 'pending'],
    ['AwaitingUnit', 'Awaiting replacement unit', 'pending'],
    ['ReadyForCollection', 'Ready for collection', 'ok'],
    ['HandedToCustomer', 'Handed to customer', 'ok'],
    ['NotAvailable', 'Replacement not available', 'err'],
    ['Cancelled', 'Cancelled — remedy changed', 'system'],
  ])('replacement %s → %s (%s)', (status, label, tone) => {
    expect(replacementStatusPresentation(status)).toEqual({ label, tone })
  })

  it('shows a returned product that does not match as a warning', () => {
    expect(returnStatePresentation('Mismatch')).toEqual({ label: 'Returned product does not match', tone: 'warn' })
  })

  it('shows a pending model proposal as awaiting approval', () => {
    expect(pendingModelProposalPresentation).toEqual({ label: 'Awaiting model approval', tone: 'warn' })
  })

  it.each<[ReplacementProductRule, string]>([
    ['SameModel', 'Same model only'],
    ['SameCategoryAtLeastClaimValue', 'Same model or approved equivalent'],
  ])('product rule %s → %s (system)', (rule, label) => {
    expect(replacementProductRulePresentation(rule)).toEqual({ label, tone: 'system' })
  })

  it.each<[ReplacementReturnRule, string]>([
    ['AtCollection', 'Returned at collection'],
    ['BeforeReservation', 'Returned before reservation'],
  ])('return rule %s → %s (system)', (rule, label) => {
    expect(replacementReturnRulePresentation(rule)).toEqual({ label, tone: 'system' })
  })

  it('never uses the AI tone, because a replacement is never an AI output', () => {
    const all = [
      ...statuses.map(replacementStatusPresentation),
      ...returnStates.map(returnStatePresentation),
      ...(['SameModel', 'SameCategoryAtLeastClaimValue'] as const).map(replacementProductRulePresentation),
      ...(['AtCollection', 'BeforeReservation'] as const).map(replacementReturnRulePresentation),
      pendingModelProposalPresentation,
    ]
    expect(all.map((value) => value.tone)).not.toContain('ai')
  })

  it('never says shipped, on its way or delivered: replacements are collected (§5)', () => {
    const labels = [...statuses.map(replacementStatusPresentation), ...returnStates.map(returnStatePresentation)].map(
      (value) => value.label,
    )
    for (const label of labels) expect(label).not.toMatch(/shipped|on its way|delivered/i)
  })

  it('shows values added to the contract later as-is', () => {
    expect(replacementStatusPresentation('InTransit' as ReplacementStatus)).toEqual({ label: 'InTransit', tone: 'pending' })
    expect(returnStatePresentation('Lost' as ReturnState)).toEqual({ label: 'Lost', tone: 'pending' })
    expect(replacementProductRulePresentation('AnyModel' as ReplacementProductRule)).toEqual({
      label: 'AnyModel',
      tone: 'system',
    })
    expect(replacementReturnRulePresentation('Never' as ReplacementReturnRule)).toEqual({ label: 'Never', tone: 'system' })
  })
})
