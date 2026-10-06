import { consistencyPresentation } from '../../shared/presentation'
import { ActorBadge, Badge, Card, ConfidenceMeter, cx, EmptyState, EvidenceTile, KeyValueList, LoadingState } from '../../shared/ui'
import tones from '../../shared/ui/tone.module.css'
import {
  evidenceKindLabel,
  findingFor,
  humanize,
  isPdf,
  useEvidenceContentUrl,
  type ClaimDetail,
  type ConsistencyRow,
  type EvidenceFinding,
  type EvidenceItem,
} from './claimDetail'
import styles from './EvidenceViewer.module.css'

/** An evidence thumbnail for the case file; images are fetched with the staff token, PDFs are not previewed. */
export function EvidenceThumbnail({ claimId, item, onOpen }: { claimId: string; item: EvidenceItem; onOpen?: () => void }) {
  const pdf = isPdf(item)
  const { url, status } = useEvidenceContentUrl(claimId, item.evidenceId, !pdf)
  return (
    <EvidenceTile
      name={item.fileName}
      kind={evidenceKindLabel(item.kind)}
      reference={item.ref}
      src={url}
      isPdf={pdf}
      status={pdf ? 'loaded' : status}
      onSelect={onOpen}
    />
  )
}

/** The original upload, shown from an object URL of the authorized content (ui-design.md §6.3). */
function OriginalUpload({ claimId, item }: { claimId: string; item: EvidenceItem }) {
  const { url, status } = useEvidenceContentUrl(claimId, item.evidenceId)
  const label = `${item.fileName} (${evidenceKindLabel(item.kind)})`
  return (
    <section className={styles.viewer} aria-label="Original upload">
      <div className={styles.viewerHeader}>
        <ActorBadge actor="system" />
        <span className={styles.viewerTitle}>Original upload</span>
        <span className={styles.fileName} title={item.fileName}>
          {item.fileName}
        </span>
      </div>
      <div className={styles.canvas}>
        {status === 'loading' && <LoadingState message="Loading file…" />}
        {status === 'error' && <EmptyState message="This file could not be loaded." />}
        {status === 'loaded' &&
          url &&
          (isPdf(item) ? (
            <object className={styles.pdf} data={url} type="application/pdf" aria-label={label}>
              <a href={url} download={item.fileName}>
                Download {item.fileName}
              </a>
            </object>
          ) : (
            <img className={styles.image} src={url} alt={label} />
          ))}
      </div>
    </section>
  )
}

const text = (value: unknown): string | undefined =>
  typeof value === 'string' ? value : typeof value === 'number' ? String(value) : undefined

function ConsistencyTable({ rows }: { rows: readonly ConsistencyRow[] }) {
  return (
    <table className={styles.consistency}>
      <caption className="visually-hidden">Consistency with the claim</caption>
      <thead>
        <tr>
          <th scope="col">Field</th>
          <th scope="col">Claim</th>
          <th scope="col">Evidence</th>
          <th scope="col">
            <span className="visually-hidden">Result</span>
          </th>
        </tr>
      </thead>
      <tbody>
        {rows.map((row) => {
          const { label, tone } = consistencyPresentation(row.match)
          return (
            <tr key={row.field}>
              <th scope="row">{humanize(row.field)}</th>
              <td>{row.claimValue ?? '—'}</td>
              <td>{row.evidenceValue ?? '—'}</td>
              <td>
                <Badge tone={tone}>{label}</Badge>
              </td>
            </tr>
          )
        })}
      </tbody>
    </table>
  )
}

/** Invoice fields the Evidence agent extracted: AI output, so labelled and to be verified. */
function InvoiceExtractionCard({ finding }: { finding: EvidenceFinding }) {
  const result = finding.result ?? {}
  const fields: Array<[string, unknown, boolean?]> = [
    ['Seller', result.sellerName],
    ['Invoice no.', result.invoiceNumber, true],
    ['Invoice date', result.invoiceDate],
    ['Product', result.productDescription],
    ['Model code', result.modelCodeOnInvoice, true],
    ['Serial', result.serialOnInvoice, true],
    ['Total', result.totalAmount !== undefined ? `${text(result.currency) ?? ''} ${text(result.totalAmount)}`.trim() : undefined],
  ]
  const anomalies = Array.isArray(result.anomalies) ? result.anomalies.filter((value): value is string => typeof value === 'string') : []
  return (
    <Card title="AI-extracted" titleAs="h3" actions={<ActorBadge actor="ai" name="Evidence agent" />} className={styles.aiCard}>
      <p className={styles.caption}>Verify before relying</p>
      <KeyValueList
        items={fields
          .filter(([, value]) => text(value) !== undefined)
          .map(([label, value, mono]) => ({ label, value: text(value), mono }))}
      />
      {anomalies.length > 0 && (
        <div className={styles.block}>
          <p className={styles.label}>Anomalies noted</p>
          <ul className={styles.list}>
            {anomalies.map((anomaly) => (
              <li key={anomaly}>{anomaly}</li>
            ))}
          </ul>
        </div>
      )}
      {finding.confidence !== undefined && <ConfidenceMeter value={finding.confidence} label="Evidence agent confidence" />}
      {(finding.consistency?.length ?? 0) > 0 && <ConsistencyTable rows={finding.consistency!} />}
    </Card>
  )
}

/** The Evidence agent's reading of a photo, with its confidence next to it. */
function PhotoInterpretationCard({ finding }: { finding: EvidenceFinding }) {
  const result = finding.result ?? {}
  const damageTypes = Array.isArray(result.damageTypes) ? result.damageTypes.filter((value): value is string => typeof value === 'string') : []
  return (
    <section className={cx(styles.interpretation, tones.ai)} aria-label="AI interpretation">
      <div className={styles.interpretationHeader}>
        <ActorBadge actor="ai" name="Evidence agent" />
        <h3 className={styles.interpretationTitle}>AI interpretation</h3>
      </div>
      {text(result.observations) && <p className={styles.observations}>{text(result.observations)}</p>}
      <KeyValueList
        items={[
          ...(damageTypes.length > 0 ? [{ label: 'Damage detected', value: damageTypes.map(humanize).join(', ') }] : []),
          ...(text(result.consistentWithDescription)
            ? [{ label: 'Vs. description', value: humanize(text(result.consistentWithDescription)!) }]
            : []),
          ...(text(result.visibleSerial) ? [{ label: 'Visible serial', value: text(result.visibleSerial), mono: true }] : []),
          ...(text(result.imageQuality) ? [{ label: 'Image quality', value: humanize(text(result.imageQuality)!) }] : []),
        ]}
      />
      {finding.confidence !== undefined && <ConfidenceMeter value={finding.confidence} label="Evidence agent confidence" />}
      {(finding.consistency?.length ?? 0) > 0 && <ConsistencyTable rows={finding.consistency!} />}
    </section>
  )
}

export interface EvidenceViewerProps {
  detail: ClaimDetail
  selectedId: string | undefined
  onSelect: (evidenceId: string) => void
}

/** Evidence tab: file list, the authorized original, and what the Evidence agent read from it (ui-design.md §6.3). */
export function EvidenceViewer({ detail, selectedId, onSelect }: EvidenceViewerProps) {
  const items = detail.evidence
  const selected = items.find((item) => item.evidenceId === selectedId) ?? items[0]
  if (!selected) return <EmptyState message="No evidence was uploaded for this claim." />

  const finding = findingFor(detail.latestEvaluation, selected)

  return (
    <div className={styles.layout}>
      <nav aria-label="Evidence files" className={styles.files}>
        <ul className={styles.fileList}>
          {items.map((item) => {
            const current = item.evidenceId === selected.evidenceId
            return (
              <li key={item.evidenceId}>
                <button
                  type="button"
                  className={cx(styles.file, current && styles.current)}
                  aria-pressed={current}
                  onClick={() => onSelect(item.evidenceId)}
                >
                  <span className={styles.fileRow}>
                    <span className={styles.fileName} title={item.fileName}>
                      {item.fileName}
                    </span>
                    {item.ref && <code className={styles.ref}>{item.ref}</code>}
                  </span>
                  <span className={styles.fileMeta}>
                    {evidenceKindLabel(item.kind)} · round {item.round}
                  </span>
                </button>
              </li>
            )
          })}
        </ul>
      </nav>
      <OriginalUpload key={selected.evidenceId} claimId={detail.claimId} item={selected} />
      <div className={styles.findings}>
        {!finding && <EmptyState message="The AI has not analysed this file." />}
        {finding?.kind === 'InvoiceExtraction' && <InvoiceExtractionCard finding={finding} />}
        {finding?.kind === 'PhotoAnalysis' && <PhotoInterpretationCard finding={finding} />}
      </div>
    </div>
  )
}
