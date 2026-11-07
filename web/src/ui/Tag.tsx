import type { HTMLAttributes, ReactNode } from 'react'
import { cx } from './cx'

/** What a colour means: an agent waits for a person, something runs, went well, went wrong, or nothing special. */
export type Tone = 'wait' | 'run' | 'ok' | 'bad' | 'neutral'

const tagTones: Record<Tone, string> = {
  wait: 'bg-wait-bg text-wait',
  run: 'bg-run-bg text-run',
  ok: 'bg-ok-bg text-ok',
  bad: 'bg-bad-bg text-bad',
  neutral: 'bg-neutral-bg text-neutral',
}

const dotTones: Record<Tone, string> = {
  wait: 'bg-wait-solid',
  run: 'bg-run-solid',
  ok: 'bg-ok-solid',
  bad: 'bg-bad-solid',
  neutral: 'bg-neutral-solid',
}

/** A small label with a tinted background, e.g. a state. */
export function Tag({
  tone = 'neutral',
  dot = false,
  className,
  children,
  ...props
}: HTMLAttributes<HTMLSpanElement> & { tone?: Tone; dot?: boolean; children: ReactNode }) {
  return (
    <span
      className={cx(
        'inline-flex h-[22px] shrink-0 items-center gap-1.5 rounded-[5px] px-2 text-xs font-medium whitespace-nowrap',
        tagTones[tone],
        className,
      )}
      {...props}
    >
      {dot && <Dot tone={tone} />}
      {children}
    </span>
  )
}

/** A coloured dot, e.g. before a state or a count. */
export function Dot({ tone = 'neutral', className }: { tone?: Tone; className?: string }) {
  return (
    <span
      aria-hidden="true"
      className={cx('inline-block size-[7px] shrink-0 rounded-full', dotTones[tone], className)}
    />
  )
}
