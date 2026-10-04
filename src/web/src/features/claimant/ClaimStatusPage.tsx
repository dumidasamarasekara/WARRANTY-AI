import { useQuery } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { Link, Navigate, useParams } from 'react-router'
import { claimantTokenFor } from '../../shared/api/claimantToken'
import { ApiProblem, api, unwrap, type Schemas } from '../../shared/api/client'
import { formatDateTime } from '../../shared/presentation'
import {
  Card,
  EmptyState,
  LoadingState,
  ProblemState,
  StatusBadge,
  Stepper,
  Timeline,
  TimelineItem,
  cx,
  type Tone,
} from '../../shared/ui'
import tones from '../../shared/ui/tone.module.css'
import { claimProgressSteps, isTransientStatus, nextSteps, statusPollIntervalMs } from './claimProgress'
import styles from './ClaimStatusPage.module.css'

type ClaimantClaimView = Schemas['ClaimantClaimView']

const accessPath = (reference: string) => `/claims/access?reference=${encodeURIComponent(reference)}`

/** Status, outcome explanation and requested items only — never risk, fraud or reasoning (FR-037). */
function OutcomeBanner({ claim }: { claim: ClaimantClaimView }) {
  let tone: Tone
  let title: string
  let body: ReactNode
  switch (claim.status) {
    case 'Approved':
      tone = 'ok'
      title = 'Your claim is approved'
      body = (
        <>
          {claim.productName && <p className={styles.product}>{claim.productName}</p>}
          {claim.outcomeExplanation && <p>{claim.outcomeExplanation}</p>}
        </>
      )
      break
    case 'Rejected':
      tone = 'err'
      title = 'Your claim was not approved'
      body = claim.outcomeExplanation && <p>{claim.outcomeExplanation}</p>
      break
    case 'PendingInformation':
      tone = 'warn'
      title = 'We need a bit more information'
      body = claim.requestedItems?.length ? (
        <>
          <p>Please send us:</p>
          <ul className={styles.requested}>
            {claim.requestedItems.map((requested, index) => (
              <li key={`${index}-${requested.item}`}>
                <strong>{requested.item}</strong> — {requested.reason}
              </li>
            ))}
          </ul>
        </>
      ) : null
      break
    case 'UnderReview':
      tone = 'pending'
      title = "We're checking your claim"
      body = <p>A member of our team is reviewing your claim. Check back here for the outcome.</p>
      break
    default:
      tone = 'pending'
      title = "We're checking your claim"
      body = <p>This usually takes a few minutes. This page updates by itself.</p>
  }

  return (
    <section className={cx(styles.banner, tones[tone])} aria-label="Claim outcome">
      <h2 className={styles.bannerTitle}>{title}</h2>
      {body && <div className={styles.bannerBody}>{body}</div>}
    </section>
  )
}

function WhatHappensNext({ claim }: { claim: ClaimantClaimView }) {
  return (
    <Card title="What happens next" titleAs="h2" padding="lg">
      <Timeline label="What happens next">
        <TimelineItem tone="ok">
          <p className={styles.stepTitle}>
            Received <span className={styles.stepMeta}>{formatDateTime(claim.submittedAt)}</span>
          </p>
          <p className={styles.stepText}>We received your claim and your files.</p>
        </TimelineItem>
        {nextSteps(claim.status).map((step) => (
          <TimelineItem key={step.key} tone={step.tone}>
            <p className={styles.stepTitle}>
              {step.title} <span className={styles.stepMeta}>{step.state}</span>
            </p>
            <p className={styles.stepText}>{step.description}</p>
          </TimelineItem>
        ))}
      </Timeline>
    </Card>
  )
}

export interface ClaimStatusPageProps {
  /** Polling interval while the claim is Submitted or Under evaluation. */
  pollIntervalMs?: number
}

/**
 * `/claims/:reference`: the claimant's view of one claim, polled until the system has finished
 * with it (ui-design.md §6.6). Without a held token the claimant is sent to the access page.
 */
export function ClaimStatusPage({ pollIntervalMs = statusPollIntervalMs }: ClaimStatusPageProps) {
  const reference = (useParams().reference ?? '').toUpperCase()
  const hasToken = claimantTokenFor(reference) !== undefined

  const claim = useQuery({
    queryKey: ['claimant-claim', reference],
    queryFn: ({ signal }) =>
      unwrap(api.GET('/api/public/claims/{reference}', { params: { path: { reference } }, signal })),
    enabled: hasToken,
    staleTime: 0,
    refetchInterval: (query) => {
      const status = query.state.data?.status
      return status && isTransientStatus(status) ? pollIntervalMs : false
    },
  })

  const unauthorized = claim.error instanceof ApiProblem && claim.error.status === 401
  if (!hasToken || unauthorized) return <Navigate to={accessPath(reference)} replace />

  if (claim.isPending) return <LoadingState message="Loading your claim…" />
  if (claim.isError) {
    if (claim.error instanceof ApiProblem && claim.error.status === 404) {
      return (
        <EmptyState
          message="We couldn't find a claim with those details."
          action={<Link to={accessPath(reference)}>Check another claim</Link>}
        />
      )
    }
    return <ProblemState problem={claim.error instanceof ApiProblem ? claim.error : {}} />
  }

  const view = claim.data
  return (
    <section className={styles.page}>
      <Stepper label="Claim progress" steps={claimProgressSteps(view.status)} />
      <div className={styles.heading}>
        <h1>Your claim</h1>
        <p className={styles.meta}>
          <code className={styles.mono} title={view.reference}>
            {view.reference}
          </code>
          <StatusBadge status={view.status} />
          {view.productName && <span>{view.productName}</span>}
        </p>
      </div>
      {/* Polling updates are announced politely (ui-design.md §6.6). */}
      <div aria-live="polite">
        <OutcomeBanner claim={view} />
      </div>
      <WhatHappensNext claim={view} />
    </section>
  )
}
