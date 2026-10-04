import { useEffect, useId, useRef, type KeyboardEvent, type ReactNode, type RefObject } from 'react'
import { createPortal } from 'react-dom'
import { Alert } from '../Alert'
import styles from './Dialog.module.css'

const focusableSelector = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled]):not([type="hidden"])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(', ')

function focusableIn(container: HTMLElement | null): HTMLElement[] {
  if (!container) return []
  return Array.from(container.querySelectorAll<HTMLElement>(focusableSelector)).filter(
    (element) => !element.closest('[hidden], [inert]'),
  )
}

export interface DialogProps {
  open: boolean
  /** Called on Esc and the close button; not while `submitting`. */
  onClose: () => void
  title: ReactNode
  description?: ReactNode
  /** Action buttons, right-aligned. */
  footer?: ReactNode
  /** Disables closing and marks the dialog busy. */
  submitting?: boolean
  /** Shown as an error alert at the top of the body. */
  error?: ReactNode
  /** Element to focus on open; defaults to the first focusable element in the body. */
  initialFocusRef?: RefObject<HTMLElement | null>
  children?: ReactNode
}

/**
 * Modal dialog labelled by its title. Traps focus while open, closes on Esc and returns focus to
 * the element that had it when the dialog opened.
 */
export function Dialog(props: DialogProps) {
  if (!props.open) return null
  return createPortal(<DialogSurface {...props} />, document.body)
}

function DialogSurface({
  onClose,
  title,
  description,
  footer,
  submitting = false,
  error,
  initialFocusRef,
  children,
}: DialogProps) {
  const titleId = useId()
  const descriptionId = useId()
  const dialogRef = useRef<HTMLDivElement>(null)
  const bodyRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const trigger = document.activeElement instanceof HTMLElement ? document.activeElement : null
    const dialog = dialogRef.current
    const initial = initialFocusRef?.current ?? focusableIn(bodyRef.current)[0] ?? focusableIn(dialog)[0] ?? dialog
    initial?.focus()

    // Pull focus back if it escapes, e.g. after a click on the backdrop.
    const keepFocusInside = (event: FocusEvent) => {
      if (dialog && event.target instanceof Node && !dialog.contains(event.target)) {
        ;(focusableIn(dialog)[0] ?? dialog).focus()
      }
    }
    document.addEventListener('focusin', keepFocusInside)

    const { overflow } = document.body.style
    document.body.style.overflow = 'hidden'

    return () => {
      document.removeEventListener('focusin', keepFocusInside)
      document.body.style.overflow = overflow
      if (trigger?.isConnected) trigger.focus()
    }
  }, [initialFocusRef])

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Escape') {
      event.stopPropagation()
      if (!submitting) onClose()
      return
    }
    if (event.key !== 'Tab') return

    const focusable = focusableIn(dialogRef.current)
    const first = focusable[0]
    const last = focusable[focusable.length - 1]
    if (!first || !last) {
      event.preventDefault()
      return
    }
    const active = document.activeElement
    if (event.shiftKey && (active === first || active === dialogRef.current)) {
      event.preventDefault()
      last.focus()
    } else if (!event.shiftKey && active === last) {
      event.preventDefault()
      first.focus()
    }
  }

  return (
    <div
      className={styles.backdrop}
      onMouseDown={(event) => {
        // Keep focus in the dialog when the backdrop is pressed.
        if (event.target === event.currentTarget) event.preventDefault()
      }}
    >
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={description ? descriptionId : undefined}
        aria-busy={submitting || undefined}
        tabIndex={-1}
        className={styles.dialog}
        onKeyDown={handleKeyDown}
      >
        <div className={styles.header}>
          <h2 id={titleId} className={styles.title}>
            {title}
          </h2>
          <button
            type="button"
            className={styles.close}
            onClick={onClose}
            disabled={submitting}
            aria-label="Close dialog"
          >
            ✕
          </button>
        </div>
        {description && (
          <p id={descriptionId} className={styles.description}>
            {description}
          </p>
        )}
        <div ref={bodyRef} className={styles.body}>
          {error && (
            <Alert tone="err" urgent>
              {error}
            </Alert>
          )}
          {children}
        </div>
        {footer && <div className={styles.footer}>{footer}</div>}
      </div>
    </div>
  )
}
