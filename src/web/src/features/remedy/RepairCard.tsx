import { useState } from 'react'
import { useMe } from '../../app/staffQueries'
import { formatDate, repairStatusPresentation } from '../../shared/presentation'
import { Alert, Badge, Button, Card } from '../../shared/ui'
import type { RepairView } from './remedyQueries'
import { RepairEventDialog } from './RepairEventDialog'
import { estimateText, failedAttemptsText, repairActionLabels, repairActions, type RepairDialogEvent } from './remedyView'
import styles from './RemedyTab.module.css'

const notPossibleReasonLabels: Record<NonNullable<RepairView['notPossibleReason']>, string> = {
  Failed: 'Repair failed',
  NotEconomical: 'Not economical',
}

export interface RepairCardProps {
  claimId: string
  repair: RepairView
  /** Claims agents and reviewers record repair updates; auditors see the card read-only. */
  canAct: boolean
  /** Reload the claim for the current repair and a fresh ETag (409 and 412). */
  onReload: () => Promise<unknown>
}

/**
 * Left column of the Remedy tab when the remedy is Repair (ui-screens.md §2): repair status, active
 * centre with its staff-only contact, hand-over date, centre reference and the event buttons.
 */
export function RepairCard({ claimId, repair, canAct, onReload }: RepairCardProps) {
  const [open, setOpen] = useState<RepairDialogEvent | null>(null)
  const [recorded, setRecorded] = useState(false)
  const status = repairStatusPresentation(repair.status)
  const centre = repair.activeCentre
  const actions = canAct ? repairActions(repair) : []
  const me = useMe()
  const estimate = estimateText(repair, me.data?.tenantCurrency)

  return (
    <>
      {recorded && <Alert tone="ok" title="Repair update recorded" />}
      <Card title="Repair" titleAs="h2">
        <div className={styles.stack}>
          <div className={styles.current}>
            <Badge tone={status.tone} dot>
              {status.label}
            </Badge>
            {repair.status === 'RepairNotPossible' && repair.notPossibleReason && (
              <span className={styles.muted}>{notPossibleReasonLabels[repair.notPossibleReason]}</span>
            )}
          </div>

          <section className={styles.section} aria-label="Repair centre">
            <h3 className={styles.sectionLabel}>Repair centre</h3>
            {centre ? (
              <dl className={styles.facts}>
                <div>
                  <dt>Name</dt>
                  <dd>{centre.name}</dd>
                </div>
                <div>
                  <dt>Address</dt>
                  <dd>{centre.address}</dd>
                </div>
                <div>
                  <dt>Opening hours</dt>
                  <dd>{centre.openingHours}</dd>
                </div>
                {centre.contact && (
                  <div>
                    <dt>Contact (staff only)</dt>
                    <dd>{centre.contact}</dd>
                  </div>
                )}
              </dl>
            ) : (
              <p className={styles.muted}>No repair centre assigned.</p>
            )}
          </section>

          <section className={styles.section} aria-label="Hand-over">
            <h3 className={styles.sectionLabel}>Hand-over</h3>
            <dl className={styles.facts}>
              <div>
                <dt>Handed over on</dt>
                <dd>{repair.handedOverOn ? formatDate(repair.handedOverOn) : '—'}</dd>
              </div>
              <div>
                <dt>Centre reference</dt>
                <dd className={styles.mono}>{repair.centreReference ?? '—'}</dd>
              </div>
            </dl>
          </section>

          <section className={styles.section} aria-label="Attempts">
            <h3 className={styles.sectionLabel}>Attempts</h3>
            <dl className={styles.facts}>
              <div>
                <dt>Failed attempts</dt>
                <dd>{failedAttemptsText(repair)}</dd>
              </div>
              {estimate && (
                <div>
                  <dt>Estimate</dt>
                  <dd>{estimate}</dd>
                </div>
              )}
            </dl>
          </section>

          {actions.length > 0 && (
            <div className={styles.actions} role="group" aria-label="Repair actions">
              {actions.map(({ type, primary }) => (
                <Button
                  key={type}
                  variant={primary ? 'primary' : 'secondary'}
                  onClick={() => {
                    setRecorded(false)
                    setOpen(type)
                  }}
                >
                  {repairActionLabels[type]}
                </Button>
              ))}
            </div>
          )}
        </div>
      </Card>
      {open && canAct && (
        <RepairEventDialog
          claimId={claimId}
          repair={repair}
          type={open}
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
