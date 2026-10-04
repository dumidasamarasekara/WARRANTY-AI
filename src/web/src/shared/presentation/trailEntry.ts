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
}

const agentActors = new Set(['intake', 'evidence', 'policy', 'decision'])

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

/** `system` → System; an agent name or an entry with AI calls → AI; anyone else (a staff `sub`) → Human. */
export function trailActorTone(entry: TrailEntryLike): Actor {
  if (entry.actor === 'system') return 'system'
  if (agentActors.has(entry.actor) || (entry.aiCalls?.length ?? 0) > 0) return 'ai'
  return 'human'
}

/** Timeline dot: the actor tone, except failed AI steps (`err`) and escalations (`human`). */
export function trailDotTone(entry: TrailEntryLike): Tone {
  if (entry.step === 'AiStepFailed') return 'err'
  if (entry.step === 'EscalatedToReview') return 'human'
  return trailActorTone(entry)
}
