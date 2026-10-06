import { useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { useMe } from '../../app/staffQueries'
import { ApiProblem } from '../../shared/api/client'
import { aiDecisionPresentation, formatDateTime, formatMoney, formatRelative } from '../../shared/presentation'
import {
  ActorBadge,
  AiPanel,
  Alert,
  Badge,
  Button,
  Card,
  ConfidenceMeter,
  cx,
  DispositionBanner,
  EmptyState,
  LoadingState,
  ProblemState,
  RiskIndicator,
  Timeline,
  TimelineItem,
  useToast,
} from '../../shared/ui'
import tones from '../../shared/ui/tone.module.css'
import { ReviewDecisionForm } from './ReviewDecisionForm'
import {
  aiDecisionAsReview,
  aiDecisionLabel,
  decidingAiDecision,
  reviewDecisionLabel,
  type ClaimDetail,
  type ReviewDecisionKind,
  type ReviewQueueItem,
} from './reviewDecision'
import { reviewClaimKey, reviewQueueKey, useReviewClaim, useReviewQueue } from './reviewQueries'
import styles from './ReviewQueuePage.module.css'

const selfReviewMessage = 'Another reviewer must decide this claim'

const problemOf = (error: unknown) => (error instanceof ApiProblem ? error : {})

/** `/staff/review/:claimId?` (`claims-reviewer`): escalated claims and the decision workspace (ui-design.md §6.4). */
export function ReviewQueuePage({ now }: { now?: Date }) {
  const { claimId } = useParams()
  const me = useMe()
  const queue = useReviewQueue()
  const currency = me.data?.tenantCurrency

  const tenant = me.data?.tenantDisplayName
  const header = (
    <div className={styles.intro}>
      <h1>Human review</h1>
      <p className={styles.subtitle}>
        Claims the AI could not safely decide on its own{tenant ? ` · ${tenant}` : ''}
      </p>
    </div>
  )

  if (queue.isPending) {
    return (
      <section className={styles.page}>
        {header}
        <LoadingState message="Loading the review queue…" />
      </section>
    )
  }
  if (queue.isError) {
    return (
      <section className={styles.page}>
        {header}
        <ProblemState problem={problemOf(queue.error)} />
      </section>
    )
  }

  const items = queue.data
  if (items.length === 0) {
    return (
      <section className={styles.page}>
        {header}
        <EmptyState message="No claims waiting for review" />
      </section>
    )
  }

  // Without a claim in the URL the oldest escalation is shown.
  const selected = claimId ? items.find((item) => item.claimId === claimId) : items[0]

  return (
    <section className={styles.page}>
      {header}
      <div className={styles.layout}>
        <nav aria-label="Review queue" className={styles.queue}>
          <ul className={styles.queueList}>
            {items.map((item) => (
              <li key={item.claimId}>
                <QueueCard item={item} selected={item.claimId === selected?.claimId} currency={currency} now={now} />
              </li>
            ))}
          </ul>
        </nav>
        <div className={styles.workspace}>
          {selected ? (
            <ReviewWorkspace key={selected.claimId} item={selected} currency={currency} />
          ) : (
            <EmptyState
              message="This claim is no longer waiting for review."
              action={<Link to="/staff/review">Back to the queue</Link>}
            />
          )}
        </div>
      </div>
    </section>
  )
}

function QueueCard({
  item,
  selected,
  currency,
  now,
}: {
  item: ReviewQueueItem
  selected: boolean
  currency: string | undefined
  now: Date | undefined
}) {
  const value = item.claimValue !== undefined && currency ? formatMoney(item.claimValue, currency) : undefined
  return (
    <Link
      to={`/staff/review/${item.claimId}`}
      className={cx(styles.card, selected && styles.cardSelected, selected && tones.human)}
      aria-current={selected ? 'page' : undefined}
    >
      <span className={styles.reference}>{item.reference}</span>
      <span className={styles.product}>{item.productName}</span>
      <span className={styles.cardMeta}>{[value, ...item.escalationReasons].filter(Boolean).join(' · ')}</span>
      <span className={styles.cardFoot}>
        <span title={formatDateTime(item.escalatedAt)}>Escalated {formatRelative(item.escalatedAt, now)}</span>
        {item.submittedByMe && <Badge tone="system">You submitted this claim</Badge>}
      </span>
    </Link>
  )
}

function ReviewWorkspace({ item, currency }: { item: ReviewQueueItem; currency: string | undefined }) {
  const claim = useReviewClaim(item.claimId)
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const toast = useToast()
  const [choosing, setChoosing] = useState<ReviewDecisionKind | null>(null)
  const [refused, setRefused] = useState(false)

  if (claim.isPending) return <LoadingState message="Loading the claim…" />
  if (claim.isError) return <ProblemState problem={problemOf(claim.error)} />

  const { detail, etag } = claim.data
  const ai = decidingAiDecision(detail)
  const blocked = Boolean(item.submittedByMe) || refused
  const value = detail.product.claimValue ?? item.claimValue

  const leaveQueue = async () => {
    setChoosing(null)
    await queryClient.invalidateQueries({ queryKey: reviewQueueKey })
    navigate('/staff/review')
  }

  return (
    <div className={styles.workspaceBody}>
      <Card padding="lg">
        <div className={styles.claimHeader}>
          <div>
            <p className={styles.headerReference}>{detail.reference}</p>
            <h2 className={styles.headerTitle}>{item.productName}</h2>
          </div>
          {value !== undefined && currency && <span className={styles.headerValue}>{formatMoney(value, currency)}</span>}
        </div>
      </Card>

      {blocked && <Alert tone="info">{selfReviewMessage}</Alert>}

      <DispositionBanner variant="human" title="Human review required" reasons={item.escalationReasons}>
        <p>You are the control point — the AI recommendation below is advisory.</p>
      </DispositionBanner>

      <div className={styles.pair}>
        <AiRecommendationCard claim={detail} />
        <YourDecisionCard claim={detail} choosing={choosing} />
      </div>

      <div className={styles.pair}>
        <RiskAndEvidenceCard claim={detail} />
        <DecisionHistoryCard claim={detail} reasons={item.escalationReasons} />
      </div>

      <div className={styles.actions} role="group" aria-label="Decision">
        <Button variant="primary" disabled={blocked} onClick={() => setChoosing('Approve')}>
          Approve
        </Button>
        <Button variant="danger" disabled={blocked} onClick={() => setChoosing('Reject')}>
          Reject
        </Button>
        <Button disabled={blocked} onClick={() => setChoosing('RequestInformation')}>
          Request more information
        </Button>
        {ai && (
          <Button
            variant="human"
            className={styles.override}
            disabled={blocked}
            onClick={() => setChoosing(ai === 'APPROVE' ? 'Reject' : 'Approve')}
          >
            Override AI recommendation…
          </Button>
        )}
      </div>

      <Link to={`/staff/claims/${detail.claimId}`} className={styles.caseFileLink}>
        Open full case file →
      </Link>

      {choosing && (
        <ReviewDecisionForm
          key={choosing}
          claim={detail}
          etag={etag}
          decision={choosing}
          onClose={() => setChoosing(null)}
          onDecided={() => {
            toast.show('Decision recorded')
            void leaveQueue()
          }}
          onSelfReviewRefused={() => {
            setChoosing(null)
            setRefused(true)
          }}
          onAlreadyDecided={() => void leaveQueue()}
          onReload={() => queryClient.refetchQueries({ queryKey: reviewClaimKey(item.claimId) })}
        />
      )}
    </div>
  )
}

function AiRecommendationCard({ claim }: { claim: ClaimDetail }) {
  const evaluation = claim.latestEvaluation
  const recommendation = evaluation?.recommendation
  const ai = decidingAiDecision(claim)
  const cited = evaluation?.policyReferences?.find((reference) => reference.cited)
  const state = evaluation?.failureReason ? 'failed' : recommendation && !recommendation.isValid ? 'invalid' : 'recommendation'

  return (
    <AiPanel title="AI recommendation" state={state}>
      {ai && recommendation ? (
        <div className={styles.recommendation}>
          <Badge tone={aiDecisionPresentation(ai).tone}>{aiDecisionLabel(ai)}</Badge>
          <ConfidenceMeter value={recommendation.confidence} size="md" />
          {cited && (
            <p className={styles.cited}>
              {cited.documentTitle} · <code>{cited.clauseKey}</code> · v{cited.version}
            </p>
          )}
        </div>
      ) : (
        <div className={styles.recommendation}>
          <p className={styles.noDecision}>No AI decision</p>
          <p className={styles.muted}>
            {recommendation?.isValid && recommendation.decision
              ? `The AI recommended ${aiDecisionPresentation(recommendation.decision).label.toLowerCase()}; the decision is yours.`
              : 'The AI could not produce a valid recommendation; the decision is yours.'}
          </p>
        </div>
      )}
    </AiPanel>
  )
}

function YourDecisionCard({ claim, choosing }: { claim: ClaimDetail; choosing: ReviewDecisionKind | null }) {
  const ai = decidingAiDecision(claim)
  const aiValue = ai ? aiDecisionAsReview(ai) : null
  return (
    <div className={cx(styles.yourDecision, tones.human)}>
      <div className={styles.yourDecisionHead}>
        <ActorBadge actor="human" />
        <h3 className={styles.cardTitle}>Your decision</h3>
      </div>
      {choosing ? (
        <p className={styles.decisionValue}>
          {aiValue && aiValue !== choosing && <s className={styles.struck}>{reviewDecisionLabel(aiValue)}</s>}{' '}
          {reviewDecisionLabel(choosing)}
        </p>
      ) : (
        <p className={styles.decisionValue}>Pending</p>
      )}
    </div>
  )
}

function RiskAndEvidenceCard({ claim }: { claim: ClaimDetail }) {
  const risk = claim.latestEvaluation?.risk
  const signals = risk?.signals ?? []
  return (
    <Card title="Risk signals & evidence">
      <div className={styles.stack}>
        {risk?.level && <RiskIndicator level={risk.level} score={risk.score} />}
        {signals.length > 0 ? (
          <ul className={styles.rows} aria-label="Risk signals">
            {signals.map((signal, index) => (
              <li key={`${signal.code}-${index}`} className={styles.row}>
                <span className={styles.rowHead}>
                  <code>{signal.code}</code>
                  <ActorBadge actor={signal.source === 'AI' ? 'ai' : 'system'} />
                  {signal.severity && <Badge tone="warn">{signal.severity}</Badge>}
                </span>
                {signal.detail && <span className={styles.muted}>{signal.detail}</span>}
              </li>
            ))}
          </ul>
        ) : (
          <p className={styles.muted}>No risk signals recorded.</p>
        )}
        <ul className={styles.rows} aria-label="Evidence">
          {claim.evidence.map((evidence) => (
            <li key={evidence.evidenceId} className={styles.row}>
              <span className={styles.rowHead}>
                {evidence.ref && <code>{evidence.ref}</code>}
                <span>{evidence.fileName}</span>
              </span>
              <span className={styles.muted}>
                {evidence.kind} · round {evidence.round}
              </span>
            </li>
          ))}
        </ul>
      </div>
    </Card>
  )
}

function DecisionHistoryCard({ claim, reasons }: { claim: ClaimDetail; reasons: readonly string[] }) {
  const recommendation = claim.latestEvaluation?.recommendation
  const aiSummary =
    recommendation?.isValid && recommendation.decision
      ? `Recommended ${aiDecisionPresentation(recommendation.decision).label.toLowerCase()} · ${recommendation.confidence}% confidence`
      : 'No valid recommendation'
  return (
    <Card title="Decision history">
      <Timeline label="Decision history">
        <TimelineItem tone="ai">
          <ActorBadge actor="ai" name="AI recommended" />
          <p className={styles.muted}>{aiSummary}</p>
        </TimelineItem>
        <TimelineItem tone="system">
          <ActorBadge actor="system" name="Guardrails escalated" />
          {reasons.length > 0 && <p className={styles.muted}>{reasons.join(' · ')}</p>}
        </TimelineItem>
        {(claim.reviewDecisions ?? []).map((decision) => (
          <TimelineItem key={decision.id} tone="human">
            <ActorBadge actor="human" name={decision.reviewerName} />
            <p className={styles.rowHead}>
              {reviewDecisionLabel(decision.decision)}
              {decision.overridesAi && <Badge tone="human">Overrode AI</Badge>}
            </p>
            <p className={styles.muted}>{formatDateTime(decision.decidedAt)}</p>
          </TimelineItem>
        ))}
      </Timeline>
    </Card>
  )
}
