import { useState } from 'react'
import { cx } from '../cx'
import styles from './EvidenceTile.module.css'

export type EvidenceTileStatus = 'loading' | 'loaded' | 'error'

export interface EvidenceTileProps {
  name: string
  /** Readable kind, e.g. "Photo" or "Invoice". */
  kind: string
  /** `EV-n` reference, shown in mono. */
  reference?: string
  /** Object URL of the authorized content, once fetched. */
  src?: string
  isPdf?: boolean
  status?: EvidenceTileStatus
  selected?: boolean
  /** Makes the tile a button, e.g. to open the viewer. */
  onSelect?: () => void
  className?: string
}

/** Evidence thumbnail: a hatched placeholder until the authorized image has loaded. */
export function EvidenceTile({
  name,
  kind,
  reference,
  src,
  isPdf = false,
  status = 'loading',
  selected = false,
  onSelect,
  className,
}: EvidenceTileProps) {
  const [imageLoaded, setImageLoaded] = useState(false)
  const showImage = !isPdf && status === 'loaded' && src

  const preview = (
    <span className={cx(styles.preview, !(showImage && imageLoaded) && styles.hatched)}>
      {showImage && (
        <img
          className={cx(styles.image, imageLoaded && styles.imageLoaded)}
          src={src}
          alt={`${name} (${kind})`}
          onLoad={() => setImageLoaded(true)}
        />
      )}
      {isPdf && status !== 'error' && <span className={styles.placeholder}>PDF</span>}
      {status === 'error' && <span className={styles.placeholder}>Preview unavailable</span>}
      {status === 'loading' && <span className="visually-hidden">Loading preview</span>}
    </span>
  )

  const caption = (
    <span className={styles.caption}>
      <span className={styles.name} title={name}>
        {name}
      </span>
      <span className={styles.meta}>
        {kind}
        {reference && <code className={styles.reference}>{reference}</code>}
      </span>
    </span>
  )

  return onSelect ? (
    <button
      type="button"
      className={cx(styles.tile, styles.button, selected && styles.selected, className)}
      onClick={onSelect}
      aria-pressed={selected}
    >
      {preview}
      {caption}
    </button>
  ) : (
    <figure className={cx(styles.tile, className)}>
      {preview}
      <figcaption>{caption}</figcaption>
    </figure>
  )
}
