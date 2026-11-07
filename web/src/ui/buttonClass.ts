import { cx } from './cx'

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger'
export type ButtonSize = 'sm' | 'md'

const base =
  'inline-flex shrink-0 cursor-pointer items-center justify-center gap-1.5 rounded-md border font-medium whitespace-nowrap transition-colors select-none hover:no-underline disabled:cursor-default disabled:opacity-50 [&_svg]:shrink-0'

const variants: Record<ButtonVariant, string> = {
  primary:
    'border-primary bg-primary text-on-primary hover:border-primary-hover hover:bg-primary-hover hover:text-on-primary',
  secondary: 'border-line-strong bg-surface text-fg hover:bg-sunken hover:text-fg',
  ghost: 'border-transparent bg-transparent text-fg-2 hover:bg-hover hover:text-fg',
  danger: 'border-transparent bg-transparent text-bad hover:bg-bad-bg hover:text-bad',
}

const sizes: Record<ButtonSize, string> = {
  sm: 'h-7 px-2 text-xs',
  md: 'h-8 px-3 text-sm',
}

/** The classes of a button, for elements that look like one (e.g. a router link). */
export function buttonClass(variant: ButtonVariant = 'secondary', size: ButtonSize = 'md') {
  return cx(base, variants[variant], sizes[size])
}
