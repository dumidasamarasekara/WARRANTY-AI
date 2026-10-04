import { useId, useState, type DragEvent, type ReactNode } from 'react'
import { formatFileSize } from '../../presentation/format'
import { cx } from '../cx'
import styles from './FileDrop.module.css'

export type FileDropItemStatus = 'uploading' | 'done' | 'error'

export interface FileDropItem {
  key: string
  name: string
  size: number
  status: FileDropItemStatus
  error?: string
}

export interface FileDropProps {
  label: ReactNode
  /** e.g. "PDF, JPG, PNG or WebP · up to 15 MB each". */
  hint?: ReactNode
  error?: ReactNode
  accept?: string
  multiple?: boolean
  disabled?: boolean
  files?: readonly FileDropItem[]
  /** Receives picked or dropped files; validation is the caller's job. */
  onFiles: (files: File[]) => void
  onRemove?: (key: string) => void
  className?: string
}

const statusText: Record<FileDropItemStatus, string> = {
  uploading: 'Uploading…',
  done: 'Added',
  error: 'Upload failed',
}

/** Drop zone backed by a real, keyboard-reachable file input. */
export function FileDrop({
  label,
  hint,
  error,
  accept,
  multiple = false,
  disabled = false,
  files = [],
  onFiles,
  onRemove,
  className,
}: FileDropProps) {
  const inputId = useId()
  const labelId = `${inputId}-label`
  const hintId = hint ? `${inputId}-hint` : undefined
  const errorId = error ? `${inputId}-error` : undefined
  const [dragOver, setDragOver] = useState(false)

  const handleDrop = (event: DragEvent<HTMLLabelElement>) => {
    event.preventDefault()
    setDragOver(false)
    if (!disabled && event.dataTransfer.files.length > 0) onFiles(Array.from(event.dataTransfer.files))
  }

  return (
    <div className={cx(styles.fileDrop, className)}>
      <span id={labelId} className={styles.label}>
        {label}
      </span>
      <label
        htmlFor={inputId}
        className={cx(styles.zone, dragOver && styles.dragOver, Boolean(error) && styles.invalid, disabled && styles.disabled)}
        onDragOver={(event) => {
          event.preventDefault()
          if (!disabled) setDragOver(true)
        }}
        onDragLeave={() => setDragOver(false)}
        onDrop={handleDrop}
      >
        <input
          id={inputId}
          type="file"
          className={cx('visually-hidden', styles.input)}
          accept={accept}
          multiple={multiple}
          disabled={disabled}
          aria-labelledby={labelId}
          aria-describedby={[hintId, errorId].filter(Boolean).join(' ') || undefined}
          aria-invalid={error ? true : undefined}
          onChange={(event) => {
            const picked = event.target.files
            if (picked && picked.length > 0) onFiles(Array.from(picked))
            event.target.value = ''
          }}
        />
        <span className={styles.prompt}>
          Drop {multiple ? 'files' : 'a file'} here or <span className={styles.browse}>browse</span>
        </span>
        {hint && (
          <span id={hintId} className={styles.hint}>
            {hint}
          </span>
        )}
      </label>
      {error && (
        <p id={errorId} className={styles.errorMessage}>
          {error}
        </p>
      )}
      {files.length > 0 && (
        <ul className={styles.files}>
          {files.map((file) => (
            <li key={file.key} className={cx(styles.file, styles[file.status])}>
              <span className={styles.fileName} title={file.name}>
                {file.name}
              </span>
              <span className={styles.fileSize}>{formatFileSize(file.size)}</span>
              <span className={styles.fileStatus}>{file.status === 'error' ? (file.error ?? statusText.error) : statusText[file.status]}</span>
              {onRemove && (
                <button
                  type="button"
                  className={styles.remove}
                  onClick={() => onRemove(file.key)}
                  aria-label={`Remove ${file.name}`}
                >
                  Remove
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
