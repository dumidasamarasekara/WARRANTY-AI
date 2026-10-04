import { useId, useState, type ComponentProps, type ReactNode } from 'react'
import { CharacterCount } from '../CharacterCount'
import { cx } from '../cx'
import styles from './Field.module.css'

/** Props the field hands to its control so label, hint and error stay connected. */
export interface FieldControlProps {
  id: string
  required?: boolean
  'aria-describedby'?: string
  'aria-invalid'?: true
}

export interface FieldProps {
  label: ReactNode
  hint?: ReactNode
  error?: ReactNode
  required?: boolean
  id?: string
  /** Rendered under the control, e.g. a `CharacterCount`. */
  footer?: ReactNode
  className?: string
  children: (control: FieldControlProps) => ReactNode
}

/** Visible label above the control; hint and error linked through `aria-describedby`. */
export function Field({ label, hint, error, required, id, footer, className, children }: FieldProps) {
  const generatedId = useId()
  const controlId = id ?? generatedId
  const hintId = hint ? `${controlId}-hint` : undefined
  const errorId = error ? `${controlId}-error` : undefined
  const describedBy = [hintId, errorId].filter(Boolean).join(' ') || undefined

  return (
    <div className={cx(styles.field, className)}>
      <label htmlFor={controlId} className={styles.label}>
        {label}
        {required && (
          <span className={styles.required} aria-hidden="true">
            {' '}
            *
          </span>
        )}
      </label>
      {children({ id: controlId, required, 'aria-describedby': describedBy, 'aria-invalid': error ? true : undefined })}
      {hint && (
        <p id={hintId} className={styles.hint}>
          {hint}
        </p>
      )}
      {error && (
        <p id={errorId} className={styles.error}>
          {error}
        </p>
      )}
      {footer}
    </div>
  )
}

interface ControlOptions {
  label: ReactNode
  hint?: ReactNode
  error?: ReactNode
  /** `md` 36 px (staff), `lg` 40 px (claimant). */
  size?: 'md' | 'lg'
}

export type TextInputProps = Omit<ComponentProps<'input'>, 'size'> & ControlOptions

export function TextInput({ label, hint, error, size = 'md', id, required, className, ...rest }: TextInputProps) {
  return (
    <Field label={label} hint={hint} error={error} required={required} id={id} className={className}>
      {(control) => <input {...rest} {...control} className={cx(styles.control, styles[size])} />}
    </Field>
  )
}

export type TextAreaProps = Omit<ComponentProps<'textarea'>, 'size'> &
  ControlOptions & {
    /** Shows a `CharacterCount`; the limits are not enforced while typing. */
    characterLimits?: { min?: number; max: number }
  }

export function TextArea({
  label,
  hint,
  error,
  size = 'md',
  id,
  required,
  className,
  characterLimits,
  value,
  defaultValue,
  onChange,
  ...rest
}: TextAreaProps) {
  const [uncontrolledLength, setUncontrolledLength] = useState(() => String(defaultValue ?? '').length)
  const length = value !== undefined ? String(value).length : uncontrolledLength

  return (
    <Field
      label={label}
      hint={hint}
      error={error}
      required={required}
      id={id}
      className={className}
      footer={
        characterLimits && <CharacterCount length={length} min={characterLimits.min} max={characterLimits.max} />
      }
    >
      {(control) => (
        <textarea
          {...rest}
          {...control}
          value={value}
          defaultValue={defaultValue}
          onChange={(event) => {
            setUncontrolledLength(event.target.value.length)
            onChange?.(event)
          }}
          className={cx(styles.control, styles.textarea, styles[size])}
        />
      )}
    </Field>
  )
}

export type SelectProps = Omit<ComponentProps<'select'>, 'size'> & ControlOptions

export function Select({ label, hint, error, size = 'md', id, required, className, children, ...rest }: SelectProps) {
  return (
    <Field label={label} hint={hint} error={error} required={required} id={id} className={className}>
      {(control) => (
        <select {...rest} {...control} className={cx(styles.control, styles.select, styles[size])}>
          {children}
        </select>
      )}
    </Field>
  )
}
