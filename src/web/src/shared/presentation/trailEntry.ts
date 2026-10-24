import type { Actor, Tone } from '../ui/tone'

export type TrailStep =
  | 'ClaimSubmitted'
  | 'TenantResolved'
  | 'EvidenceStored'
  | 'IntakeValidated'
  | 'ClaimExtracted'
  | 'CustomerVerified'
  | 'ProductIdentified'
  | 'PolicyRetrieved'
  | 'EvidenceAnalyzed'
  | 'CoverageAssessed'
  | 'RiskEvaluated'
  | 'AiRecommended'
  | 'GuardrailsEvaluated'
  | 'AutoApproved'
  | 'AutoRejected'
  | 'InformationRequested'
  | 'EscalatedToReview'
  | 'ReviewerDecided'
  | 'SupplementReceived'
  | 'ActionExecuted'
  | 'AiStepFailed'
  | 'Correction'
  // Remedies and the repair hand-over (002 data-model.md)
  | 'RemedyRecommended'
  | 'RemedyDecided'
  | 'RepairCentreAssigned'
  | 'RepairHandedOver'
  | 'RepairStatusUpdated'
  | 'RepairAttemptEnded'
  | 'RemedyCompleted'
  // Replacement fulfilment and the faulty-product return (003 data-model.md)
  | 'ReplacementLocationAssigned'
  | 'FaultyProductReturned'
  | 'ReplacementModelProposed'
  | 'ReplacementModelDecided'
  | 'ReplacementStatusUpdated'
  | 'ReplacementUnitRegistered'

const stepLabels: Record<TrailStep, string> = {
  ClaimSubmitted: 'Claim submitted',
  TenantResolved: 'Tenant resolved',
  EvidenceStored: 'Evidence stored',
  IntakeValidated: 'Intake validated',
  ClaimExtracted: 'Claim details extracted',
  CustomerVerified: 'Customer verified',
  ProductIdentified: 'Product identified',
  PolicyRetrieved: 'Policy retrieved',
  EvidenceAnalyzed: 'Evidence analysed',
  CoverageAssessed: 'Coverage assessed',
  RiskEvaluated: 'Risk evaluated',
  AiRecommended: 'AI recommendation',
  GuardrailsEvaluated: 'Guardrails evaluated',
  AutoApproved: 'Auto-approved',
  AutoRejected: 'Auto-rejected',
  InformationRequested: 'Information requested',
  EscalatedToReview: 'Escalated to human review',
  ReviewerDecided: 'Reviewer decision',
  SupplementReceived: 'Supplement received',
  ActionExecuted: 'Action executed',
  AiStepFailed: 'AI step failed',
  Correction: 'Correction',
  // The remedy comes from the tenant's remedy table, never from the AI (FR-014), hence "from policy".
  RemedyRecommended: 'Remedy from policy',
  RemedyDecided: 'Remedy decided',
  RepairCentreAssigned: 'Repair centre assigned',
  RepairHandedOver: 'Handed over to repair centre',
  RepairStatusUpdated: 'Repair status updated',
  RepairAttemptEnded: 'Repair attempt ended',
  RemedyCompleted: 'Remedy completed',
  ReplacementLocationAssigned: 'Replacement location assigned',
  FaultyProductReturned: 'Faulty product returned',
  ReplacementModelProposed: 'Replacement model proposed',
  ReplacementModelDecided: 'Replacement model decided',
  ReplacementStatusUpdated: 'Replacement status updated',
  ReplacementUnitRegistered: 'Replacement unit registered',
}

const agentActors = new Set(['intake', 'evidence', 'policy', 'decision'])

// The background adjudication principal and the deterministic risk step act without a person.
const serviceActors = new Set(['adjudication-service', 'risk'])

/** The parts of a `TraceEntry` the presentation needs. */
export interface TrailEntryLike {
  step: string
  actor: string
  aiCalls?: readonly unknown[]
}

/** Readable label for a trail step; steps without a label (the contract allows new ones) show as-is. */
export function trailStepLabel(step: string): string {
  return stepLabels[step as TrailStep] ?? step
}

/**
 * `system` → System; an agent name or an entry with AI calls → AI; the adjudication service or the
 * deterministic risk step → System; anyone else (a staff `sub`, the claimant) → Human.
 */
export function trailActorTone(entry: TrailEntryLike): Actor {
  if (entry.actor === 'system') return 'system'
  if (agentActors.has(entry.actor) || (entry.aiCalls?.length ?? 0) > 0) return 'ai'
  if (serviceActors.has(entry.actor)) return 'system'
  return 'human'
}

/** Timeline dot: the actor tone, except failed AI steps (`err`) and escalations (`human`). */
export function trailDotTone(entry: TrailEntryLike): Tone {
  if (entry.step === 'AiStepFailed') return 'err'
  if (entry.step === 'EscalatedToReview') return 'human'
  return trailActorTone(entry)
}
