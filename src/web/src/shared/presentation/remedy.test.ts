import { describe, expect, expectTypeOf, it } from 'vitest'
import type { Schemas } from '../api/client'
import {
  remedyStatePresentation,
  remedyTypePresentation,
  repairStatusPresentation,
  type RemedyState,
  type RemedyType,
  type RepairStatus,
} from './remedy'

describe('remedy and repair values (002 ui-screens.md §1)', () => {
  it('declares exactly the values of the REST contract', () => {
    // Checked by `tsc -b` in the build: a value added to or removed from the contract fails it.
    expectTypeOf<RemedyType>().toEqualTypeOf<Schemas['RemedyType']>()
    expectTypeOf<RemedyState>().toEqualTypeOf<Schemas['RemedyState']>()
    expectTypeOf<RepairStatus>().toEqualTypeOf<Schemas['RepairStatus']>()
  })

  it.each<[RemedyState, string, string]>([
    ['AwaitingDecision', 'Awaiting remedy decision', 'warn'],
    ['InProgress', 'In progress', 'system'],
    ['Completed', 'Completed', 'ok'],
  ])('remedy %s → %s (%s)', (state, label, tone) => {
    expect(remedyStatePresentation(state)).toEqual({ label, tone })
  })

  it.each<[RepairStatus, string, string]>([
    ['AwaitingHandover', 'Awaiting hand-over', 'pending'],
    ['AtRepairCentre', 'At repair centre', 'system'],
    ['ReadyForCollection', 'Ready for collection', 'ok'],
    ['Collected', 'Collected', 'ok'],
    ['RepairNotPossible', 'Repair not possible', 'err'],
  ])('repair %s → %s (%s)', (status, label, tone) => {
    expect(repairStatusPresentation(status)).toEqual({ label, tone })
  })

  it.each<RemedyType>(['Repair', 'Replacement', 'Refund'])('remedy type %s is a system value', (remedy) => {
    expect(remedyTypePresentation(remedy)).toEqual({ label: remedy, tone: 'system' })
  })

  it('never uses the AI tone, because a remedy is never an AI output (FR-014)', () => {
    const all = [
      ...(['Repair', 'Replacement', 'Refund'] as const).map(remedyTypePresentation),
      ...(['AwaitingDecision', 'InProgress', 'Completed'] as const).map(remedyStatePresentation),
      ...(['AwaitingHandover', 'AtRepairCentre', 'ReadyForCollection', 'Collected', 'RepairNotPossible'] as const).map(
        repairStatusPresentation,
      ),
    ]
    expect(all.map((value) => value.tone)).not.toContain('ai')
  })

  it('shows values added to the contract later as-is', () => {
    expect(remedyTypePresentation('StoreCredit' as RemedyType)).toEqual({ label: 'StoreCredit', tone: 'system' })
    expect(remedyStatePresentation('OnHold' as RemedyState)).toEqual({ label: 'OnHold', tone: 'pending' })
    expect(repairStatusPresentation('InTransit' as RepairStatus)).toEqual({ label: 'InTransit', tone: 'pending' })
  })
})
