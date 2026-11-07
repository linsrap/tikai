import type { ReactNode } from 'react'
import { cx } from './cx'

export type SegmentedOption<T extends string> = { value: T; label: ReactNode; count?: number }

/** A group of toggle buttons where one is on (filters, view modes). Each button carries aria-pressed. */
export function Segmented<T extends string>({
  label,
  value,
  options,
  onChange,
  size = 'md',
  className,
}: {
  label: string
  value: T
  options: SegmentedOption<T>[]
  onChange: (value: T) => void
  size?: 'sm' | 'md'
  className?: string
}) {
  return (
    <div
      role="group"
      aria-label={label}
      className={cx('inline-flex shrink-0 gap-0.5 rounded-[7px] bg-track p-0.5', className)}
    >
      {options.map((option) => {
        const on = option.value === value
        return (
          <button
            key={option.value}
            type="button"
            aria-pressed={on}
            onClick={() => onChange(option.value)}
            className={cx(
              'inline-flex cursor-pointer items-center gap-1.5 rounded-[5px] px-2.5 font-medium whitespace-nowrap transition-colors',
              size === 'sm' ? 'h-6 text-xs' : 'h-7 text-sm',
              on
                ? 'bg-surface text-fg shadow-[0_1px_2px_rgb(0_0_0/0.06),0_0_0_1px_var(--c-line)]'
                : 'text-fg-muted hover:text-fg',
            )}
          >
            {option.label}
            {option.count != null && (
              <span className="text-fg-subtle tabular-nums">{option.count}</span>
            )}
          </button>
        )
      })}
    </div>
  )
}
