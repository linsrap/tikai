import {
  forwardRef,
  type InputHTMLAttributes,
  type LabelHTMLAttributes,
  type ReactNode,
  type SelectHTMLAttributes,
  type TextareaHTMLAttributes,
} from 'react'
import { cx } from './cx'

const control =
  'w-full rounded-md border border-line-strong bg-surface px-2.5 text-base text-fg placeholder:text-fg-faint transition-colors hover:border-fg-faint focus-visible:border-focus focus-visible:outline-1 focus-visible:outline-offset-0 disabled:cursor-not-allowed disabled:opacity-60'

export const Input = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement>>(
  function Input({ className, ...props }, ref) {
    return <input ref={ref} className={cx(control, 'h-8', className)} {...props} />
  },
)

export const Textarea = forwardRef<
  HTMLTextAreaElement,
  TextareaHTMLAttributes<HTMLTextAreaElement> & { mono?: boolean }
>(function Textarea({ className, mono = false, ...props }, ref) {
  return (
    <textarea
      ref={ref}
      className={cx(
        control,
        'min-h-20 resize-y py-1.5 leading-5',
        mono && 'font-mono text-mono',
        className,
      )}
      {...props}
    />
  )
})

export const Select = forwardRef<HTMLSelectElement, SelectHTMLAttributes<HTMLSelectElement>>(
  function Select({ className, ...props }, ref) {
    return (
      <select
        ref={ref}
        className={cx(control, 'h-8 w-auto max-w-full pr-7', className)}
        {...props}
      />
    )
  },
)

/**
 * A label around its control, with an optional hint under it. The label text is the control's accessible name, so it
 * stays the same as before the redesign.
 */
export function Field({
  label,
  hint,
  children,
  className,
  ...props
}: Omit<LabelHTMLAttributes<HTMLLabelElement>, 'children'> & {
  label: ReactNode
  hint?: ReactNode
  children: ReactNode
}) {
  return (
    <label className={cx('flex flex-col gap-1.5', className)} {...props}>
      <span className="text-sm font-medium text-fg-2">{label}</span>
      {children}
      {hint && <span className="text-sm text-fg-subtle">{hint}</span>}
    </label>
  )
}

/** A checkbox or radio with its label to the right. */
export function Check({
  label,
  hint,
  className,
  ...props
}: Omit<InputHTMLAttributes<HTMLInputElement>, 'className'> & {
  label: ReactNode
  hint?: ReactNode
  className?: string
}) {
  return (
    <label className={cx('flex cursor-pointer items-start gap-2 text-base', className)}>
      <input className="mt-[3px] size-3.5 shrink-0 accent-(--c-primary)" {...props} />
      <span className="flex flex-col">
        <span>{label}</span>
        {hint && <span className="text-sm text-fg-subtle">{hint}</span>}
      </span>
    </label>
  )
}

/** An error message from a request or a form. */
export function ErrorText({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <p role="alert" className={cx('m-0 text-sm text-bad', className)}>
      {children}
    </p>
  )
}

/** Quiet help text. */
export function Hint({ children, className }: { children: ReactNode; className?: string }) {
  return <p className={cx('m-0 text-sm text-fg-subtle', className)}>{children}</p>
}
