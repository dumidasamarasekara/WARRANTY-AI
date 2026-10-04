import { createContext, useContext } from 'react'

export interface ToastApi {
  /** Shows a confirmation for at least 6 s. Never the only place an action is offered. */
  show(message: string, options?: { durationMs?: number }): void
}

export const ToastContext = createContext<ToastApi | null>(null)

export function useToast(): ToastApi {
  const toast = useContext(ToastContext)
  if (!toast) throw new Error('useToast must be used inside a ToastProvider')
  return toast
}
