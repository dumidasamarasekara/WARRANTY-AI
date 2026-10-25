import { useState } from 'react'
import { replacementStatusPresentation } from '../../shared/presentation'
import { ActorBadge, Alert, Badge, Button, Card, cx } from '../../shared/ui'
import tones from '../../shared/ui/tone.module.css'
import type { RemedyView, ReplacementView } from './remedyQueries'
import { ReplacementEventDialog, type UnitModel } from './ReplacementEventDialog'
import { replacementActionLabels, replacementActions, replacementPolicyBasis, type ReplacementDialogEvent } from './replacementView'
import styles from './RemedyTab.module.css'

export interface ReplacementCardProps {
  claimId: string
  replacement: ReplacementView
  /** The claim's remedy: its policy version is the basis of the replacement rules. */
  remedy: Pick<RemedyView, 'policyVersion'>
  /** The claimed product's model. */
  claimedModel: UnitModel
  /** Claims agents and reviewers record replacement updates; auditors see the card read-only. */
  canAct: boolean
  /** Reload the claim for the current replacement and a fresh ETag (409 and 412). */
  onReload: () => Promise<unknown>
}

/**
 * Left column of the Remedy tab when the claim has a replacement (003 ui-screens.md §2): replacement status, active
 * location with its staff-only contact, the policy basis, the current unit and the event buttons. It replaces 002's
 * completion card for a Replacement; the hand-over to the customer completes the remedy.
 */
export function ReplacementCard({ claimId, replacement, remedy, claimedModel, canAct, onReload }: ReplacementCardProps) {
  const [open, setOpen] = useState<ReplacementDialogEvent | null>(null)
  const [recorded, setRecorded] = useState(false)
  const status = replacementStatusPresentation(replacement.status)
  const location = replacement.activeLocation
  const unit = replacement.currentUnit
  const actions = canAct ? replacementActions(replacement) : []

  return (
    <>
      {recorded && <Alert tone="ok" title="Replacement update recorded" />}
      <Card title="Replacement" titleAs="h2">
        <div className={styles.stack}>
          <div className={styles.current}>
            <Badge tone={status.tone} dot>
              {status.label}
            </Badge>
          </div>
          <p className={cx(styles.origin, tones.system)}>
            <ActorBadge actor="system" />
            <span>{replacementPolicyBasis(remedy, replacement)}</span>
          </p>

          <section className={styles.section} aria-label="Location">
            <h3 className={styles.sectionLabel}>Location</h3>
            {location ? (
              <dl className={styles.facts}>
                <div>
                  <dt>Name</dt>
                  <dd>{location.name}</dd>
                </div>
                <div>
                  <dt>Address</dt>
                  <dd>{location.address}</dd>
                </div>
                <div>
                  <dt>Opening hours</dt>
                  <dd>{location.openingHours}</dd>
                </div>
                {location.contact && (
                  <div>
                    <dt>Contact (staff only)</dt>
                    <dd>{location.contact}</dd>
                  </div>
                )}
              </dl>
            ) : (
              <p className={styles.muted}>No location assigned.</p>
            )}
          </section>

          <section className={styles.section} aria-label="Unit">
            <h3 className={styles.sectionLabel}>Unit</h3>
            <dl className={styles.facts}>
              <div>
                <dt>Claimed model</dt>
                <dd className={styles.mono}>{claimedModel.modelCode}</dd>
              </div>
              <div>
                <dt>Reserved unit</dt>
                <dd>
                  {unit ? (
                    <>
                      <span className={styles.mono}>{unit.modelCode}</span> · <span className={styles.mono}>{unit.serialNumber}</span>
                    </>
                  ) : (
                    '—'
                  )}
                </dd>
              </div>
            </dl>
          </section>

          {actions.length > 0 && (
            <div className={styles.actions} role="group" aria-label="Replacement actions">
              {actions.map(({ type, primary }) => (
                <Button
                  key={type}
                  variant={primary ? 'primary' : 'secondary'}
                  onClick={() => {
                    setRecorded(false)
                    setOpen(type)
                  }}
                >
                  {replacementActionLabels[type]}
                </Button>
              ))}
            </div>
          )}
        </div>
      </Card>
      {open && canAct && (
        <ReplacementEventDialog
          claimId={claimId}
          replacement={replacement}
          type={open}
          claimedModel={claimedModel}
          onClose={() => setOpen(null)}
          onRecorded={() => {
            setRecorded(true)
            setOpen(null)
          }}
          onReload={onReload}
        />
      )}
    </>
  )
}
