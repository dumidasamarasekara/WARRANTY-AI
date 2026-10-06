import type { Schemas } from '../../shared/api/client'
import { cx } from '../../shared/ui'
import type { TraceEntry } from './decisionTrace'
import styles from './TraceEntryDetails.module.css'

type AiCall = Schemas['AiCallSummary']
type ToolCall = NonNullable<TraceEntry['toolCalls']>[number]
type RagQuery = NonNullable<TraceEntry['ragQueries']>[number]

const count = new Intl.NumberFormat('en-US')

const tokens = (value: number | undefined) => (value === undefined ? '—' : count.format(value))
const latency = (value: number | undefined) => (value === undefined ? '—' : `${count.format(value)} ms`)
/** Provider prices are in US dollars; AI calls cost fractions of a cent, so four decimals. */
const cost = (value: number | undefined) => (value === undefined ? '—' : `USD ${value.toFixed(4)}`)

function Field({ label, value, className }: { label: string; value: string; className?: string }) {
  return (
    <div className={styles.field}>
      <dt>{label}</dt>
      <dd className={className}>{value}</dd>
    </div>
  )
}

function AiCallRow({ call }: { call: AiCall }) {
  const retried = (call.attempt ?? 1) > 1
  return (
    <li className={styles.row}>
      <dl className={styles.fields}>
        <Field label="Agent" value={call.agent ?? '—'} />
        <Field label="Model" value={call.model ?? '—'} />
        <Field label="Prompt" value={call.promptVersion ?? '—'} />
        <Field label="Input tokens" value={tokens(call.inputTokens)} />
        <Field label="Output tokens" value={tokens(call.outputTokens)} />
        <Field label="Cache tokens" value={tokens(call.cacheReadTokens)} />
        <Field label="Latency" value={latency(call.latencyMs)} />
        <Field label="Cost (est.)" value={cost(call.estimatedCost)} />
        <Field label="Status" value={call.status ?? '—'} className={cx(call.status && call.status !== 'Ok' && styles.failed)} />
        <Field label="Attempt" value={String(call.attempt ?? 1)} />
      </dl>
      {retried && <span className={styles.retried}>↻ retried</span>}
    </li>
  )
}

function ToolCallRow({ call }: { call: ToolCall }) {
  return (
    <li className={styles.row}>
      <dl className={styles.fields}>
        <Field label="Tool" value={call.tool ?? '—'} />
        <Field
          label="Allowed"
          value={call.allowed === undefined ? '—' : call.allowed ? 'yes' : 'no — denied'}
          className={cx(call.allowed === false && styles.failed)}
        />
        <Field label="Latency" value={latency(call.latencyMs)} />
      </dl>
      {call.summary && <p className={styles.summary}>{call.summary}</p>}
    </li>
  )
}

function RagQueryRow({ query }: { query: RagQuery }) {
  const filters = query.filters && Object.keys(query.filters).length > 0 ? JSON.stringify(query.filters) : undefined
  return (
    <li className={styles.row}>
      <dl className={styles.fields}>
        <Field label="Namespaces" value={query.namespaces?.join(', ') || '—'} />
        <Field label="Result clauses" value={query.resultClauseKeys?.join(', ') || 'none'} />
        <Field label="Latency" value={latency(query.latencyMs)} />
        {filters && <Field label="Filters" value={filters} />}
      </dl>
    </li>
  )
}

export interface TraceEntryDetailsProps {
  entry: TraceEntry
  /** Element ID, so a disclosure button can point at it with `aria-controls`. */
  id?: string
}

/**
 * The technical view of one trace entry (ui-design.md §6.3 "Decision trace"): its AI calls, tool
 * calls and RAG queries, the correlation ID and the stored step details, in a mono well.
 */
export function TraceEntryDetails({ entry, id }: TraceEntryDetailsProps) {
  const aiCalls = entry.aiCalls ?? []
  const toolCalls = entry.toolCalls ?? []
  const ragQueries = entry.ragQueries ?? []
  const details = entry.details && Object.keys(entry.details).length > 0 ? entry.details : undefined

  return (
    <div id={id} className={styles.well}>
      {aiCalls.length > 0 && (
        <section aria-label="AI calls" className={styles.group}>
          <h4 className={styles.heading}>AI calls</h4>
          <ul className={styles.list}>
            {aiCalls.map((call, index) => (
              <AiCallRow key={index} call={call} />
            ))}
          </ul>
        </section>
      )}
      {toolCalls.length > 0 && (
        <section aria-label="Tool calls" className={styles.group}>
          <h4 className={styles.heading}>Tool calls</h4>
          <ul className={styles.list}>
            {toolCalls.map((call, index) => (
              <ToolCallRow key={index} call={call} />
            ))}
          </ul>
        </section>
      )}
      {ragQueries.length > 0 && (
        <section aria-label="RAG queries" className={styles.group}>
          <h4 className={styles.heading}>RAG queries</h4>
          <ul className={styles.list}>
            {ragQueries.map((query, index) => (
              <RagQueryRow key={index} query={query} />
            ))}
          </ul>
        </section>
      )}
      {entry.correlationId && (
        <p className={styles.correlation}>
          Correlation ID <code>{entry.correlationId}</code>
        </p>
      )}
      {details && (
        <section aria-label="Step details" className={styles.group}>
          <h4 className={styles.heading}>Step details</h4>
          <pre className={styles.json}>{JSON.stringify(details, null, 2)}</pre>
        </section>
      )}
    </div>
  )
}
