import type { ReactNode } from 'react'
import { Link, useParams, useSearchParams } from 'react-router'
import { useMe } from '../../app/staffQueries'
import { useStaffSession } from '../../app/useStaffSession'
import { ApiProblem } from '../../shared/api/client'
import {
  aiDecisionPresentation,
  checkResultPresentation,
  consistencyPresentation,
  coverageLabel,
  dispositionPresentation,
  escalationReasonLabel,
  formatDate,
  formatDateTime,
  formatMoney,
  riskSignalSourceActor,
  type Disposition,
} from '../../shared/presentation'
import {
  ActorBadge,
  AiPanel,
  Alert,
  Badge,
  Card,
  ConfidenceMeter,
  cx,
  DecidedByBadge,
  DispositionBanner,
  EmptyState,
  FactorRow,
  KeyValueList,
  LoadingState,
  PolicyCitation,
  ProblemState,
  RiskIndicator,
  StatusBadge,
  Tabs,
  type TabItem,
} from '../../shared/ui'
import tones from '../../shared/ui/tone.module.css'
import styles from './ClaimDetailPage.module.css'
import {
  humanize,
  progressSteps,
  useClaimDetail,
  type ClaimDetail,
  type ClaimEvaluation,
  type ProgressStep,
  type Recommendation,
} from './claimDetail'
import { DecisionTracePage } from '../trace/DecisionTracePage'
import { EvidenceThumbnail, EvidenceViewer } from './EvidenceViewer'

type TabKey = 'case' | 'decision' | 'evidence' | 'trace'

const tabKeys: readonly TabKey[] = ['case', 'decision', 'evidence', 'trace']

interface Context {
  detail: ClaimDetail
  basePath: string
  currency: string | undefined
  tenantName: string
  isReviewer: boolean
}

const reasonLabels = (evaluation: ClaimEvaluation | undefined) => (evaluation?.guardrails?.reasons ?? []).map(escalationReasonLabel)

const dispositionTitles: Record<Disposition, string> = {
  AutoApprove: 'Auto-approved by the system after guardrails passed',
  AutoReject: 'Auto-rejected by the system after guardrails passed',
  RequestInformation: 'Information requested from the customer',
  HumanReview: 'Escalated — a reviewer must decide',
}

/** The guardrails' outcome route with its reasons by their readable labels. */
function OutcomeRoute({ evaluation }: { evaluation: ClaimEvaluation | undefined }) {
  const disposition = evaluation?.guardrails?.disposition
  if (!disposition) return null
  const human = disposition === 'HumanReview'
  return (
    <DispositionBanner
      variant={human ? 'human' : 'ai'}
      title={dispositionTitles[disposition]}
      label="Outcome route"
      tag={human ? undefined : <ActorBadge actor="system" />}
      reasons={reasonLabels(evaluation)}
    />
  )
}

function ProgressStrip({ steps }: { steps: readonly ProgressStep[] }) {
  return (
    <ol className={styles.progress} aria-label="Claim progress">
      {steps.map((step) => (
        <li
          key={step.key}
          className={cx(styles.progressStep, styles[step.state], step.state !== 'pending' && tones[step.actor])}
          aria-current={step.state === 'current' ? 'step' : undefined}
        >
          <span className={styles.progressMarker} aria-hidden="true">
            {step.state === 'done' ? '✓' : ''}
          </span>
          <span className={styles.progressLabel}>
            {step.label}
            <span className="visually-hidden">
              {' '}
              ({step.state === 'done' ? 'completed' : step.state === 'current' ? 'current step' : 'not started'})
            </span>
          </span>
        </li>
      ))}
    </ol>
  )
}

function ClaimHeader({ detail, currency, steps }: { detail: ClaimDetail; currency: string | undefined; steps: readonly ProgressStep[] }) {
  const risk = detail.latestEvaluation?.risk
  const facts: Array<{ label: string; value: ReactNode }> = [
    {
      label: 'Claim value',
      value: detail.product.claimValue !== undefined && currency ? formatMoney(detail.product.claimValue, currency) : '—',
    },
    { label: 'Customer', value: detail.customer.fullName ?? '—' },
    { label: 'Region', value: detail.region ?? '—' },
  ]
  if (risk?.level) {
    facts.push({ label: 'Risk', value: <RiskIndicator level={risk.level} score={risk.score} /> })
  }
  return (
    <Card padding="lg" className={styles.header}>
      <div className={styles.headerTop}>
        <div className={styles.identity}>
          <p className={styles.reference}>{detail.reference}</p>
          <h1 className={styles.productName}>{detail.product.name ?? detail.product.modelCode ?? 'Unknown product'}</h1>
          <div className={styles.badges}>
            <StatusBadge status={detail.status} />
            {detail.finalDecidedBy && <DecidedByBadge decidedBy={detail.finalDecidedBy} />}
          </div>
        </div>
        <dl className={styles.facts}>
          {facts.map((fact) => (
            <div key={fact.label} className={styles.fact}>
              <dt>{fact.label}</dt>
              <dd>{fact.value}</dd>
            </div>
          ))}
        </dl>
      </div>
      <ProgressStrip steps={steps} />
    </Card>
  )
}

/** The decision word in the AI's tone; only ever rendered inside an AI panel. */
function DecisionWord({ recommendation, large = false }: { recommendation: Recommendation | undefined; large?: boolean }) {
  if (!recommendation?.decision || !recommendation.isValid) {
    return <p className={cx(styles.decisionWord, large && styles.display)}>No AI decision</p>
  }
  const { label, tone } = aiDecisionPresentation(recommendation.decision)
  return (
    <p className={cx(styles.decisionWord, large && styles.display, tones[tone], styles.toned)}>
      {label}
    </p>
  )
}

function AiStateAlerts({ evaluation }: { evaluation: ClaimEvaluation | undefined }) {
  const recommendation = evaluation?.recommendation
  return (
    <>
      {recommendation && !recommendation.isValid && (
        <Alert tone="err" title="The AI recommendation failed validation">
          {(recommendation.validationErrors?.length ?? 0) > 0 && (
            <ul className={styles.list}>
              {recommendation.validationErrors!.map((error) => (
                <li key={error}>{error}</li>
              ))}
            </ul>
          )}
        </Alert>
      )}
      {evaluation?.failureReason && (
        <Alert tone="warn" title="AI analysis could not be completed — routed to human review">
          {evaluation.failureReason}
        </Alert>
      )}
    </>
  )
}

function CaseFile({ detail, basePath, tenantName }: Context) {
  const evaluation = detail.latestEvaluation
  const recommendation = evaluation?.recommendation
  const extraction = evaluation?.extraction
  const firstCitation = evaluation?.policyReferences?.find((reference) => reference.cited)
  const finalized = detail.finalDecidedBy
  const checks = evaluation?.guardrails?.checks ?? []
  const purchase = detail.purchase

  return (
    <div className={styles.caseFile}>
      <div className={styles.column}>
        {detail.status === 'PendingInformation' && (detail.requestedItems?.length ?? 0) > 0 && (
          <Alert tone="warn" title="Waiting for the customer to send more information">
            <ul className={styles.list}>
              {detail.requestedItems!.map((item) => (
                <li key={item.item}>
                  <strong>{humanize(item.item)}</strong> — {item.reason}
                </li>
              ))}
            </ul>
          </Alert>
        )}
        <Card title="Customer">
          <KeyValueList
            items={[
              { label: 'Name', value: detail.customer.fullName ?? '—' },
              { label: 'Email', value: detail.customer.email ?? '—' },
              { label: 'Country', value: detail.customer.country ?? '—' },
            ]}
          />
        </Card>
        <Card title="Product & purchase">
          <KeyValueList
            items={[
              { label: 'Product', value: detail.product.name ?? '—' },
              { label: 'Model code', value: detail.product.modelCode ?? '—', mono: true },
              { label: 'Serial', value: detail.product.serialNumber ?? '—', mono: true },
              { label: 'Category', value: detail.product.category ?? '—' },
              { label: 'In catalog', value: detail.product.inCatalog ? 'Yes' : 'No' },
              { label: 'Purchase date', value: purchase.date ? formatDate(purchase.date) : '—' },
              { label: 'Place', value: purchase.place ?? '—' },
              {
                label: 'Price',
                value: purchase.price !== undefined && purchase.currency ? formatMoney(purchase.price, purchase.currency) : '—',
              },
            ]}
          />
        </Card>
        <Card title="Claim">
          <KeyValueList
            items={[
              { label: 'Claim date', value: formatDate(detail.claimDate) },
              { label: 'Region', value: detail.region ?? '—' },
              { label: 'Channel', value: detail.channel === 'AgentPortal' ? 'Agent portal' : 'Claimant portal' },
            ]}
          />
          <figure className={styles.quote}>
            <blockquote>“{detail.problemDescription}”</blockquote>
            <figcaption>Customer-written text · original evidence</figcaption>
          </figure>
        </Card>
        {extraction && (
          <AiPanel title="Extracted from the description" meta="Intake agent">
            <KeyValueList
              items={[
                ...(typeof extraction.problemCategory === 'string'
                  ? [{ label: 'Problem', value: humanize(extraction.problemCategory) }]
                  : []),
                ...(typeof extraction.component === 'string' ? [{ label: 'Component', value: humanize(extraction.component) }] : []),
                ...(typeof extraction.claimedCause === 'string'
                  ? [{ label: 'Claimed cause', value: humanize(extraction.claimedCause) }]
                  : []),
                ...(Array.isArray(extraction.symptoms) && extraction.symptoms.length > 0
                  ? [{ label: 'Symptoms', value: extraction.symptoms.join(', ') }]
                  : []),
                ...(typeof extraction.summary === 'string' ? [{ label: 'Summary', value: extraction.summary }] : []),
              ]}
            />
          </AiPanel>
        )}
        <Card
          title="Uploaded evidence"
          actions={
            detail.evidence.length > 0 && (
              <Link to={`${basePath}/evidence`} className={styles.textLink}>
                Open viewer →
              </Link>
            )
          }
        >
          {detail.evidence.length === 0 ? (
            <EmptyState message="No evidence was uploaded." />
          ) : (
            <div className={styles.evidenceGrid}>
              {detail.evidence.map((item) => (
                <EvidenceThumbnail key={item.evidenceId} claimId={detail.claimId} item={item} />
              ))}
            </div>
          )}
        </Card>
        <ReviewDecisions detail={detail} />
      </div>
      <aside className={cx(styles.column, styles.sticky)} aria-label="AI summary">
        {recommendation ? (
          <AiPanel
            title={finalized ? `Recommendation · finalized by ${finalized === 'System' ? 'system' : 'reviewer'}` : 'Recommendation · not final'}
            meta={tenantName}
            state={recommendation.isValid ? 'recommendation' : 'invalid'}
          >
            <DecisionWord recommendation={recommendation} />
            {recommendation.confidence !== undefined && <ConfidenceMeter value={recommendation.confidence} />}
            {evaluation?.risk?.level && (
              <p className={styles.inline}>
                <span className={styles.metricLabel}>Risk</span>
                <RiskIndicator level={evaluation.risk.level} score={evaluation.risk.score} />
              </p>
            )}
            {recommendation.coverage && (
              <p className={styles.inline}>
                <span className={styles.metricLabel}>Policy match</span>
                {coverageLabel(recommendation.coverage)}
              </p>
            )}
            {firstCitation && (
              <p className={styles.inline}>
                <span className={styles.metricLabel}>Cites</span>
                <code className={styles.mono}>{firstCitation.clauseKey}</code>
                <span>
                  {firstCitation.clauseTitle ?? firstCitation.documentTitle} · v{firstCitation.version}
                </span>
              </p>
            )}
            {checks.length > 0 && (
              <div className={styles.factors}>
                {checks.slice(0, 3).map((check) => (
                  <FactorRow key={check.code} tone={check.passed ? 'ok' : 'err'}>
                    {check.message ?? humanize(check.code)}
                  </FactorRow>
                ))}
              </div>
            )}
          </AiPanel>
        ) : (
          <AiPanel title="Recommendation" state={evaluation?.failureReason ? 'failed' : 'recommendation'}>
            <p>{evaluation?.failureReason ? 'AI analysis could not be completed.' : 'The AI has not made a recommendation yet.'}</p>
          </AiPanel>
        )}
        <OutcomeRoute evaluation={evaluation} />
        {evaluation && (
          <Link to={`${basePath}/decision`} className={styles.secondaryLink}>
            Open AI decision
          </Link>
        )}
      </aside>
    </div>
  )
}

/** Human decisions: solid, blue, with the reviewer and time; an override shows the AI's word struck through. */
function ReviewDecisions({ detail }: { detail: ClaimDetail }) {
  const decisions = detail.reviewDecisions ?? []
  if (decisions.length === 0) return null
  const aiDecision = detail.latestEvaluation?.recommendation?.decision
  return (
    <Card title="Review decisions">
      <ol className={styles.decisions}>
        {decisions.map((decision) => (
          <li key={decision.id} className={cx(styles.decision, tones.human)}>
            <p className={styles.decisionHead}>
              <ActorBadge actor="human" name={decision.reviewerName} />
              <strong>{decision.decision === 'RequestInformation' ? 'Requested information' : decision.decision === 'Approve' ? 'Approved' : 'Rejected'}</strong>
              {decision.overridesAi && aiDecision && (
                <span className={styles.override}>
                  overrides AI <s>{aiDecisionPresentation(aiDecision).label}</s>
                </span>
              )}
              <span className={styles.time}>{formatDateTime(decision.decidedAt)}</span>
            </p>
            {decision.claimantExplanation && (
              <p>
                <span className={styles.metricLabel}>Message to the claimant</span> {decision.claimantExplanation}
              </p>
            )}
            {decision.justification && (
              <p>
                <span className={styles.metricLabel}>Internal reason</span> {decision.justification}
              </p>
            )}
            {(decision.requestedItems?.length ?? 0) > 0 && (
              <ul className={styles.list}>
                {decision.requestedItems!.map((item) => (
                  <li key={item.item}>
                    {humanize(item.item)} — {item.reason}
                  </li>
                ))}
              </ul>
            )}
          </li>
        ))}
      </ol>
    </Card>
  )
}

function Metric({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className={styles.metric}>
      <p className={styles.metricLabel}>{label}</p>
      {children}
    </div>
  )
}

function AiDecisionTab({ detail, basePath, tenantName, isReviewer }: Context) {
  const evaluation = detail.latestEvaluation
  if (!evaluation) return <EmptyState message="The AI has not evaluated this claim yet." />

  const recommendation = evaluation.recommendation
  const risk = evaluation.risk
  const checks = evaluation.guardrails?.checks
  const disposition = evaluation.guardrails?.disposition
  const assessment = evaluation.policyAssessment
  const references = evaluation.policyReferences ?? []
  const findings = evaluation.evidenceFindings ?? []
  const evidenceByRef = new Map(detail.evidence.filter((item) => item.ref).map((item) => [item.ref!, item]))
  const meta = recommendation?.model ? `${recommendation.model} · ${recommendation.promptVersion ?? ''}`.replace(/ · $/, '') : undefined

  return (
    <div className={styles.tabBody}>
      <OutcomeRoute evaluation={evaluation} />
      <AiStateAlerts evaluation={evaluation} />
      <div className={styles.decisionLayout}>
        <div className={styles.column}>
          <AiPanel
            title="AI decision"
            titleAs="h2"
            variant="full"
            meta={meta}
            state={!recommendation ? 'failed' : recommendation.isValid ? 'recommendation' : 'invalid'}
          >
            <div className={styles.metrics}>
              <Metric label="Recommendation">
                <DecisionWord recommendation={recommendation} large />
              </Metric>
              <Metric label="Confidence">
                {recommendation?.confidence !== undefined ? <ConfidenceMeter value={recommendation.confidence} size="md" /> : <p>—</p>}
              </Metric>
              {risk?.level && (
                <Metric label="Risk">
                  <RiskIndicator level={risk.level} score={risk.score} />
                </Metric>
              )}
              <Metric label="Policy match">
                <p className={styles.metricValue}>{recommendation?.coverage ? coverageLabel(recommendation.coverage) : '—'}</p>
              </Metric>
            </div>
            {(recommendation?.reasoningSummary || (checks?.length ?? 0) > 0) && (
              <section className={styles.section} aria-label="Why — decision factors">
                <h3 className={styles.sectionLabel}>Why — decision factors</h3>
                {recommendation?.reasoningSummary && <p>{recommendation.reasoningSummary}</p>}
                {checks?.map((check) => {
                  const { label } = checkResultPresentation(check.passed)
                  return (
                    <FactorRow key={check.code} tone={check.passed ? 'ok' : 'err'} statusLabel={label}>
                      <span className={styles.checkHead}>
                        <code className={styles.mono}>{check.code}</code>
                        {check.message && <span>{check.message}</span>}
                      </span>
                      {!check.passed && (check.expected || check.actual) && (
                        <span className={styles.expected}>
                          Expected {check.expected ?? '—'} · actual {check.actual ?? '—'}
                        </span>
                      )}
                    </FactorRow>
                  )
                })}
              </section>
            )}
            {(recommendation?.evidenceRefs?.length ?? 0) > 0 && (
              <section className={styles.section} aria-label="Evidence references">
                <h3 className={styles.sectionLabel}>Evidence references</h3>
                <ul className={styles.refList}>
                  {recommendation!.evidenceRefs!.map((reference, index) => {
                    const item = reference.ref ? evidenceByRef.get(reference.ref) : undefined
                    const finding = findings.find((candidate) => candidate.ref === reference.ref)
                    const conflicts = finding?.consistency?.some((row) => !row.match)
                    const consistency = finding?.consistency?.length ? consistencyPresentation(!conflicts) : undefined
                    return (
                      <li key={`${reference.ref}-${index}`} className={styles.refRow}>
                        {item ? (
                          <Link to={`${basePath}/evidence?item=${encodeURIComponent(item.evidenceId)}`} className={styles.chip}>
                            {reference.ref}
                          </Link>
                        ) : (
                          <code className={styles.chip}>{reference.ref}</code>
                        )}
                        <span>{reference.observation}</span>
                        {consistency && <Badge tone={consistency.tone}>{consistency.label}</Badge>}
                      </li>
                    )
                  })}
                </ul>
              </section>
            )}
          </AiPanel>
        </div>
        <div className={styles.column}>
          <Card
            title="Policy evidence"
            actions={
              <Link to="/staff/policies" className={styles.textLink}>
                View policy versions →
              </Link>
            }
          >
            <div className={styles.stack}>
              {assessment?.confidence !== undefined && (
                <div className={styles.agentConfidence}>
                  <ActorBadge actor="ai" name="Policy agent" />
                  <ConfidenceMeter value={assessment.confidence} label="Policy agent confidence" />
                </div>
              )}
              {references.length === 0 && <p className={styles.muted}>No policy clauses were retrieved.</p>}
              {references.map((reference) => (
                <PolicyCitation
                  key={reference.ref}
                  documentTitle={reference.documentTitle}
                  clauseKey={reference.clauseKey}
                  clauseTitle={reference.clauseTitle}
                  version={reference.version}
                  effectiveFrom={reference.effectiveFrom}
                  effectiveTo={reference.effectiveTo}
                  excerpt={reference.excerpt}
                  tenantName={tenantName}
                  cited={reference.cited}
                />
              ))}
            </div>
          </Card>
          <Card title="Action">
            {disposition === 'HumanReview' ? (
              <div className={styles.stack}>
                <p>Escalated — a reviewer must decide.</p>
                {isReviewer && detail.status === 'UnderReview' && (
                  <Link to={`/staff/review/${encodeURIComponent(detail.claimId)}`} className={styles.humanLink}>
                    Open in review
                  </Link>
                )}
              </div>
            ) : disposition ? (
              <p className={styles.inline}>
                <ActorBadge actor="system" />
                <span>
                  {dispositionPresentation(disposition).label} · executed after guardrails passed
                </span>
              </p>
            ) : (
              <p className={styles.muted}>No action has been taken yet.</p>
            )}
          </Card>
          {risk && (
            <Card title="Risk signals">
              {(risk.signals?.length ?? 0) === 0 ? (
                <p className={styles.muted}>No risk signals.</p>
              ) : (
                <ul className={styles.signals}>
                  {risk.signals!.map((signal, index) => (
                    <li key={`${signal.code}-${index}`} className={styles.signal}>
                      <span className={styles.signalHead}>
                        <code className={styles.mono}>{signal.code}</code>
                        {signal.source && <ActorBadge actor={riskSignalSourceActor(signal.source)} />}
                        {signal.severity && <Badge tone={signal.severity === 'High' ? 'err' : signal.severity === 'Medium' ? 'warn' : 'system'}>{signal.severity}</Badge>}
                      </span>
                      {signal.detail && <span className={styles.muted}>{signal.detail}</span>}
                    </li>
                  ))}
                </ul>
              )}
            </Card>
          )}
          <Card title="Model information">
            <KeyValueList
              items={[
                { label: 'Decision', value: recommendation?.model ?? '—', mono: true },
                { label: 'Prompt', value: recommendation?.promptVersion ?? '—', mono: true },
                ...(assessment?.model ? [{ label: 'Policy agent', value: `${assessment.model} · ${assessment.promptVersion ?? ''}`, mono: true }] : []),
              ]}
            />
          </Card>
        </div>
      </div>
    </div>
  )
}

function EvidenceTab({ detail }: Context) {
  const [search, setSearch] = useSearchParams()
  return (
    <EvidenceViewer
      detail={detail}
      selectedId={search.get('item') ?? undefined}
      onSelect={(evidenceId) => setSearch({ item: evidenceId }, { replace: true })}
    />
  )
}

/** `/staff/claims/:claimId/(case|decision|evidence|trace)`: the claim workspace (ui-design.md §6.3, FR-033). */
export function ClaimDetailPage() {
  const params = useParams()
  const claimId = params.claimId ?? ''
  const tabParam = (params['*'] ?? '').split('/')[0] as TabKey
  const session = useStaffSession()
  const me = useMe()
  const claim = useClaimDetail(claimId)

  if (claim.isPending) return <LoadingState message="Loading claim…" />
  if (claim.isError) {
    return <ProblemState problem={claim.error instanceof ApiProblem ? claim.error : {}} action={<Link to="/staff/claims">Back to claims</Link>} />
  }

  const roles = session?.roles ?? []
  const isReviewer = roles.includes('claims-reviewer')
  const canTrace = isReviewer || roles.includes('auditor')
  const detail = claim.data
  const basePath = `/staff/claims/${encodeURIComponent(detail.claimId)}`
  const tab: TabKey = tabKeys.includes(tabParam) && (tabParam !== 'trace' || canTrace) ? tabParam : 'case'

  const tabs: TabItem[] = [
    { key: 'case', label: 'Case file', to: `${basePath}/case` },
    { key: 'decision', label: 'AI decision', to: `${basePath}/decision` },
    { key: 'evidence', label: 'Evidence', to: `${basePath}/evidence` },
  ]
  if (canTrace) tabs.push({ key: 'trace', label: 'Decision trace', to: `${basePath}/trace` })

  const context: Context = {
    detail,
    basePath,
    currency: me.data?.tenantCurrency,
    tenantName: me.data?.tenantDisplayName ?? 'This tenant',
    isReviewer,
  }

  return (
    <section className={styles.page}>
      <ClaimHeader detail={detail} currency={context.currency} steps={progressSteps(detail)} />
      <Tabs items={tabs} currentKey={tab} label="Claim sections" link={Link} />
      {tab === 'case' && <CaseFile {...context} />}
      {tab === 'decision' && <AiDecisionTab {...context} />}
      {tab === 'evidence' && <EvidenceTab {...context} />}
      {tab === 'trace' && <DecisionTracePage claimId={detail.claimId} reference={detail.reference} />}
    </section>
  )
}
