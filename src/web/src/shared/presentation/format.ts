const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
const dateOnly = /^(\d{4})-(\d{2})-(\d{2})$/

type DateInput = string | Date

const pad = (value: number) => String(value).padStart(2, '0')

/** Date-only values (`2026-10-02`) are calendar dates, not instants: read them without a time zone shift. */
function toDate(value: DateInput): Date {
  if (value instanceof Date) return value
  const match = dateOnly.exec(value)
  if (match) return new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]))
  return new Date(value)
}

/** `02 Oct 2026` */
export function formatDate(value: DateInput): string {
  const date = toDate(value)
  return `${pad(date.getDate())} ${months[date.getMonth()]} ${date.getFullYear()}`
}

/** 24-hour `09:41` */
export function formatTime(value: DateInput): string {
  const date = toDate(value)
  return `${pad(date.getHours())}:${pad(date.getMinutes())}`
}

/** `02 Oct 2026, 09:41` */
export function formatDateTime(value: DateInput): string {
  return `${formatDate(value)}, ${formatTime(value)}`
}

/** `09:41:07` for decision trace entries (render in mono). */
export function formatTraceTime(value: DateInput): string {
  const date = toDate(value)
  return `${formatTime(date)}:${pad(date.getSeconds())}`
}

/** Relative time for queues only ("2 h ago"); put the absolute `formatDateTime` in a `title`. */
export function formatRelative(value: DateInput, now: Date = new Date()): string {
  const minutes = Math.floor((now.getTime() - toDate(value).getTime()) / 60_000)
  if (minutes < 1) return 'just now'
  if (minutes < 60) return `${minutes} min ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours} h ago`
  return `${Math.floor(hours / 24)} d ago`
}

/** `USD 1,400.00`: claim values in `Me.tenantCurrency`, purchase prices in `purchase.currency`. */
export function formatMoney(amount: number, currency: string): string {
  return new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency,
    currencyDisplay: 'code',
  }).format(amount)
}

/** `2.4 MB` for upload rows. */
export function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}
