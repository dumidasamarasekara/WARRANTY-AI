import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { ToastContext, type ToastApi } from './toastContext'
import styles from './Toast.module.css'

const minimumDurationMs = 6000

interface ToastEntry {
  id: number
  message: string
  durationMs: number
}

/** Hosts the bottom-right toast stack in a polite live region. Mount once around the app. */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastEntry[]>([])
  const nextId = useRef(0)

  const dismiss = useCallback((id: number) => setToasts((current) => current.filter((toast) => toast.id !== id)), [])

  const api = useMemo<ToastApi>(
    () => ({
      show(message, options) {
        const id = ++nextId.current
        const durationMs = Math.max(minimumDurationMs, options?.durationMs ?? minimumDurationMs)
        setToasts((current) => [...current, { id, message, durationMs }])
      },
    }),
    [],
  )

  return (
    <ToastContext value={api}>
      {children}
      <div className={styles.region} role="status" aria-live="polite">
        {toasts.map((toast) => (
          <Toast key={toast.id} {...toast} onDismiss={dismiss} />
        ))}
      </div>
    </ToastContext>
  )
}

function Toast({ id, message, durationMs, onDismiss }: ToastEntry & { onDismiss: (id: number) => void }) {
  useEffect(() => {
    const timer = setTimeout(() => onDismiss(id), durationMs)
    return () => clearTimeout(timer)
  }, [id, durationMs, onDismiss])

  return (
    <div className={styles.toast}>
      <span className={styles.message}>{message}</span>
      <button type="button" className={styles.dismiss} onClick={() => onDismiss(id)} aria-label="Dismiss notification">
        ✕
      </button>
    </div>
  )
}
