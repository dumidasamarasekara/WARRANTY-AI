import { useEffect, useRef, useState } from 'react'
import { formatDate, replacementStatusPresentation, returnStatePresentation } from '../../shared/presentation'
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
  /** The claim's serial, next to the serial seen on a returned product that does not match it. */
  claimedSerial?: string
  /** The repair centre holding the faulty product when the replacement follows a 002 repair (FR-020). */
  holdingCentreName?: string
  /** Opened from a review-queue `ReplacementCheck`: the card takes the focus. */
  focused?: boolean
  /** Claims agents and reviewers record replacement updates; auditors see the card read-only. */
  canAct: boolean
  /** Reload the claim for the current replacement and a fresh ETag (409 and 412). */
  onReload: () => Promise<unknown>
}

/**
 * The faulty-product return line (ui-screens.md §2): the return state, the date, the serial seen with a `warn` badge
 * when it does not match the claim's serial, or the repair centre holding the product (FR-020). Staff-only.
 */
function ReturnSection({
  replacement,
  claimedSerial,
  holdingCentreName,
}: Pick<ReplacementCardProps, 'replacement' | 'claimedSerial' | 'holdingCentreName'>) {
  const state = returnStatePresentation(replacement.returnState)
  const faultyReturn = replacement.faultyProductReturn
  const held = replacement.returnState === 'HeldByRepairCentre' || faultyReturn?.heldByRepairCentre === true
  const mismatch = faultyReturn?.serialSeen !== undefined && faultyReturn.serialMatches === false

  return (
    <section className={styles.section} aria-label="Return">
      <h3 className={styles.sectionLabel}>Faulty product return</h3>
      <div className={styles.current}>
        <Badge tone={state.tone} dot>
          {state.label}
        </Badge>
      </div>
      {held && holdingCentreName && (
        <p className={cx(styles.origin, tones.system)}>
          <ActorBadge actor="system" />
          <span>Held by repair centre — {holdingCentreName}</span>
        </p>
      )}
      {faultyReturn && (
        <dl className={styles.facts}>
          <div>
            <dt>{held ? 'Handed to the repair centre on' : 'Returned on'}</dt>
            <dd>{faultyReturn.returnedOn ? formatDate(faultyReturn.returnedOn) : '—'}</dd>
          </div>
          {faultyReturn.serialSeen !== undefined && (
            <div>
              <dt>Serial seen</dt>
              <dd className={styles.badges}>
                <span className={styles.mono}>{faultyReturn.serialSeen}</span>
                {mismatch && <Badge tone="warn">Does not match the claim</Badge>}
              </dd>
            </div>
          )}
          {mismatch && claimedSerial && (
            <div>
              <dt>Claim serial</dt>
              <dd className={styles.mono}>{claimedSerial}</dd>
            </div>
          )}
        </dl>
      )}
    </section>
  )
}

/**
 * Left column of the Remedy tab when the claim has a replacement (003 ui-screens.md §2): replacement status, active
 * location with its staff-only contact, the policy basis, the faulty-product return, the current unit and the event
 * buttons. It replaces 002's completion card for a Replacement; the hand-over to the customer completes the remedy.
 */
export function ReplacementCard({
  claimId,
  replacement,
  remedy,
  claimedModel,
  claimedSerial,
  holdingCentreName,
  focused = false,
  canAct,
  onReload,
}: ReplacementCardProps) {
  const [open, setOpen] = useState<ReplacementDialogEvent | null>(null)
  const [recorded, setRecorded] = useState(false)
  const cardRef = useRef<HTMLDivElement>(null)
  const status = replacementStatusPresentation(replacement.status)
  const location = replacement.activeLocation
  const unit = replacement.currentUnit
  const actions = canAct ? replacementActions(replacement) : []

  useEffect(() => {
    if (focused) cardRef.current?.focus()
  }, [focused])

  return (
    <>
      {recorded && <Alert tone="ok" title="Replacement update recorded" />}
      <div ref={cardRef} tabIndex={-1} className={styles.focusTarget}>
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

            <ReturnSection replacement={replacement} claimedSerial={claimedSerial} holdingCentreName={holdingCentreName} />

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
      </div>
      {open && canAct && (
        <ReplacementEventDialog
          claimId={claimId}
          replacement={replacement}
          type={open}
          claimedModel={claimedModel}
          claimedSerial={claimedSerial}
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
