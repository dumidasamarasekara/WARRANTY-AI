import { useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useSearchParams } from 'react-router'
import { formatDateTime, remedyStatePresentation } from '../../shared/presentation'
import { ActorBadge, Alert, Badge, Button, Card, cx, EmptyState, Timeline, TimelineItem } from '../../shared/ui'
import tones from '../../shared/ui/tone.module.css'
import { claimQueryKey, type ClaimDetail } from '../claims/claimDetail'
import { CompletionCard } from './CompletionCard'
import { RemedyDialog } from './RemedyDialog'
import { RepairCard } from './RepairCard'
import { RepairEventsTimeline } from './RepairEventsTimeline'
import type { RemedyHistoryEntry, RemedyView } from './remedyQueries'
import { decideRemedyParam, isCompletableRemedy, pendingReasonText, remedyLabel, remedyList, remedyOrigin, scenarioLabel } from './remedyView'
import styles from './RemedyTab.module.css'

export interface RemedyTabProps {
  detail: ClaimDetail
  /** Only reviewers decide a remedy; agents and auditors see the card read-only. */
  isReviewer: boolean
  /** Claims agents and reviewers record repair updates and completions; auditors see them read-only. */
  canRecordRepair?: boolean
}

function HistoryEntry({ entry }: { entry: RemedyHistoryEntry }) {
  const human = entry.decidedBy === 'Reviewer'
  const what =
    entry.kind === 'Decided'
      ? entry.remedy
        ? `${remedyLabel(entry.remedy)} decided`
        : 'Remedy decided'
      : entry.remedy
        ? `${remedyLabel(entry.remedy)} set by policy`
        : `Policy allows ${remedyList(entry.allowedRemedies)}`
  return (
    <TimelineItem tone={human ? 'human' : 'system'}>
      <p className={styles.historyHead}>
        <ActorBadge actor={human ? 'human' : 'system'} name={entry.actor} />
        <strong>{what}</strong>
        <time className={styles.time} dateTime={entry.createdAt}>
          {formatDateTime(entry.createdAt)}
        </time>
      </p>
      <p className={styles.muted}>
        {scenarioLabel(entry.scenario)} · allowed {remedyList(entry.allowedRemedies)}
      </p>
      {entry.reason && <p>{entry.reason}</p>}
    </TimelineItem>
  )
}

function RemedyCard({ remedy, canDecide, onDecide }: { remedy: RemedyView; canDecide: boolean; onDecide: () => void }) {
  const state = remedyStatePresentation(remedy.state)
  const origin = remedyOrigin(remedy)
  const awaiting = remedy.state === 'AwaitingDecision'

  return (
    <Card title="Remedy" titleAs="h2">
      <div className={styles.stack}>
        <div className={styles.current}>
          <p className={styles.remedyName}>{remedy.remedy ? remedyLabel(remedy.remedy) : 'No remedy yet'}</p>
          <Badge tone={state.tone} dot>
            {state.label}
          </Badge>
        </div>
        {origin && (
          <p className={cx(styles.origin, tones[origin.actor])}>
            <ActorBadge actor={origin.actor} />
            <span>
              {remedyLabel(remedy.remedy!)} · {origin.text}
            </span>
          </p>
        )}

        {awaiting && (
          <Alert
            tone="warn"
            title="Awaiting remedy decision"
            action={
              canDecide && (
                <Button variant="primary" onClick={onDecide}>
                  Decide remedy
                </Button>
              )
            }
          >
            {pendingReasonText(remedy)}
          </Alert>
        )}

        <section className={styles.section} aria-label="Policy basis">
          <h3 className={styles.sectionLabel}>Policy basis</h3>
          <dl className={styles.facts}>
            <div>
              <dt>Policy</dt>
              <dd className={styles.mono}>
                {remedy.policyVersion.policyCode} v{remedy.policyVersion.version}
              </dd>
            </div>
            <div>
              <dt>Table row</dt>
              <dd className={styles.mono}>{remedy.tableRow}</dd>
            </div>
            <div>
              <dt>Scenario</dt>
              <dd>{scenarioLabel(remedy.scenario)}</dd>
            </div>
            <div>
              <dt>Allowed remedies</dt>
              <dd className={styles.badges}>
                {remedy.allowedRemedies.length === 0
                  ? '—'
                  : remedy.allowedRemedies.map((allowed) => (
                      <Badge key={allowed} tone="system">
                        {remedyLabel(allowed)}
                      </Badge>
                    ))}
              </dd>
            </div>
          </dl>
        </section>

        <section className={styles.section} aria-label="Remedy history">
          <h3 className={styles.sectionLabel}>History</h3>
          {remedy.history.length === 0 ? (
            <p className={styles.muted}>No remedy decisions yet.</p>
          ) : (
            <Timeline label="Remedy history">
              {[...remedy.history]
                .sort((a, b) => a.seq - b.seq)
                .map((entry) => (
                  <HistoryEntry key={entry.seq} entry={entry} />
                ))}
            </Timeline>
          )}
        </section>
      </div>
    </Card>
  )
}

/**
 * `/staff/claims/:claimId/remedy` (002 ui-screens.md §2): the remedy card with its origin line, policy
 * basis, history and, while a reviewer must choose, the `warn` banner with **Decide remedy** for
 * reviewers. The remedy is never an AI output (FR-014): no violet, no AI tag. When the remedy is Repair,
 * the repair card follows and the right column holds the repair events; for Refund, and for a Replacement without a
 * replacement (decided before 003), the completion card follows; a claim's replacement has its own card (003 T332).
 */
export function RemedyTab({ detail, isReviewer, canRecordRepair = false }: RemedyTabProps) {
  const queryClient = useQueryClient()
  const [search, setSearch] = useSearchParams()
  const [decided, setDecided] = useState(false)
  const remedy = detail.remedy
  const canDecide = isReviewer && remedy?.state === 'AwaitingDecision'
  const dialogOpen = canDecide && search.has(decideRemedyParam)

  // After a failed or uneconomical repair the remedy waits for a reviewer with no remedy set; the repair
  // (its attempts and estimate) stays visible until another remedy is decided.
  const repair = remedy?.remedy === 'Repair' || remedy?.state === 'AwaitingDecision' ? detail.repair : undefined
  const completable = remedy && remedy.state !== 'AwaitingDecision' && isCompletableRemedy(remedy.remedy) ? { ...remedy, remedy: remedy.remedy } : undefined
  const reload = () => queryClient.refetchQueries({ queryKey: claimQueryKey(detail.claimId) })

  if (!remedy) return <EmptyState message="This claim has no remedy yet. The remedy is set when the claim is approved." />

  const openDialog = () => {
    setDecided(false)
    setSearch((current) => {
      const next = new URLSearchParams(current)
      next.set(decideRemedyParam, '')
      return next
    })
  }
  const closeDialog = () =>
    setSearch(
      (current) => {
        const next = new URLSearchParams(current)
        next.delete(decideRemedyParam)
        return next
      },
      { replace: true },
    )

  return (
    <div className={styles.layout}>
      <div className={styles.column}>
        {decided && <Alert tone="ok" title="Remedy decided" />}
        <RemedyCard remedy={remedy} canDecide={canDecide} onDecide={openDialog} />
        {repair && <RepairCard claimId={detail.claimId} repair={repair} canAct={canRecordRepair} onReload={reload} />}
        {completable && (
          <CompletionCard
            claimId={detail.claimId}
            remedy={completable}
            canAct={canRecordRepair}
            hasReplacement={detail.replacement !== undefined}
            onReload={reload}
          />
        )}
      </div>
      <div className={styles.column}>{repair && <RepairEventsTimeline events={repair.events} />}</div>
      {dialogOpen && (
        <RemedyDialog
          claimId={detail.claimId}
          remedy={remedy}
          onClose={closeDialog}
          onDecided={() => {
            setDecided(true)
            closeDialog()
          }}
          onReload={reload}
        />
      )}
    </div>
  )
}
