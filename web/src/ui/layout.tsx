import type { FormHTMLAttributes, HTMLAttributes, ReactNode } from 'react'
import { cx } from './cx'

/** The content column of a page under its header. */
export function PageBody({
  children,
  wide = false,
  className,
}: {
  children: ReactNode
  wide?: boolean
  className?: string
}) {
  return (
    <div
      className={cx(
        'px-4 pt-5 pb-12 sm:px-10',
        wide ? 'max-w-[1240px]' : 'max-w-[1040px]',
        className,
      )}
    >
      {children}
    </div>
  )
}

/**
 * The top of a page: a breadcrumb, the title with actions to its right, an optional line under it and optional tabs.
 * With tabs it sits on the surface colour with a bottom border; without, it blends into the page.
 */
export function PageHeader({
  crumbs,
  title,
  titleExtra,
  subtitle,
  actions,
  tabs,
  wide = false,
}: {
  crumbs?: ReactNode
  title: ReactNode
  titleExtra?: ReactNode
  subtitle?: ReactNode
  actions?: ReactNode
  tabs?: ReactNode
  wide?: boolean
}) {
  return (
    <header className={cx(tabs != null && 'border-b border-line bg-surface')}>
      <div
        className={cx(
          'px-4 pt-5 sm:px-10',
          tabs == null && 'pb-0',
          wide ? 'max-w-[1240px]' : 'max-w-[1040px]',
        )}
      >
        {crumbs && (
          <nav
            aria-label="Breadcrumb"
            className="mb-1 flex flex-wrap items-center gap-1.5 text-sm text-fg-subtle [&_a]:text-fg-muted"
          >
            {crumbs}
          </nav>
        )}
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <h1 className="m-0 text-xl font-semibold tracking-[-0.015em]">{title}</h1>
              {titleExtra}
            </div>
            {subtitle && <div className="mt-0.5 text-base text-fg-muted">{subtitle}</div>}
          </div>
          {actions && <div className="ml-auto flex flex-wrap items-center gap-2">{actions}</div>}
        </div>
        {tabs && (
          <nav aria-label="Sections" className="mt-3.5 flex gap-5 overflow-x-auto">
            {tabs}
          </nav>
        )}
      </div>
    </header>
  )
}

/** A slash between breadcrumb items. */
export function CrumbSep() {
  return <span aria-hidden="true">/</span>
}

/** A bordered surface: lists, tables and forms sit on one. */
export function Panel({
  className,
  children,
  ...props
}: HTMLAttributes<HTMLElement> & { children: ReactNode }) {
  return (
    <section className={cx('rounded-lg border border-line bg-surface', className)} {...props}>
      {children}
    </section>
  )
}

/** A panel's title row. */
export function PanelHeader({
  title,
  children,
  className,
}: {
  title: ReactNode
  children?: ReactNode
  className?: string
}) {
  return (
    <div
      className={cx(
        'flex flex-wrap items-center gap-2.5 border-b border-line-soft px-4 py-2.5',
        className,
      )}
    >
      <h2 className="m-0 text-sm font-semibold">{title}</h2>
      {children}
    </div>
  )
}

/** A small heading above a group of items, with an optional dot or count. */
export function SectionLabel({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <h2
      className={cx(
        'mt-7 mb-2.5 flex items-center gap-2 text-xs font-semibold text-fg-muted first:mt-0',
        className,
      )}
    >
      {children}
    </h2>
  )
}

/** What a list shows when it has nothing. */
export function EmptyState({
  title,
  children,
  className,
}: {
  title: ReactNode
  children?: ReactNode
  className?: string
}) {
  return (
    <div
      className={cx(
        'rounded-lg border border-dashed border-line-strong px-6 py-10 text-center',
        className,
      )}
    >
      <p className="m-0 font-medium text-fg">{title}</p>
      {children && <div className="mx-auto mt-1 max-w-md text-sm text-fg-muted">{children}</div>}
    </div>
  )
}

/** A key on the keyboard, e.g. in a hint. */
export function Kbd({ children }: { children: ReactNode }) {
  return (
    <kbd className="inline-flex h-[18px] min-w-[18px] items-center justify-center rounded-[4px] border border-line-strong bg-surface px-1 font-mono text-[11px] text-fg-2">
      {children}
    </kbd>
  )
}

/** Shown while a query loads for the first time. */
export function Loading({ label = 'Loading…' }: { label?: string }) {
  return <p className="m-0 text-sm text-fg-subtle">{label}</p>
}

/**
 * One block of a settings page: title and explanation on the left, the content (a panel or form) on the right; stacked
 * on narrow screens.
 */
export function SettingsSection({
  title,
  description,
  children,
  id,
}: {
  title: ReactNode
  description?: ReactNode
  children: ReactNode
  id?: string
}) {
  return (
    <section
      id={id}
      aria-label={typeof title === 'string' ? title : undefined}
      className="grid gap-x-10 gap-y-4 border-t border-line py-8 first:border-t-0 first:pt-2 lg:grid-cols-[15rem_minmax(0,1fr)]"
    >
      <div>
        <h2 className="m-0 text-base font-semibold">{title}</h2>
        {description && <div className="mt-1 text-sm text-fg-muted">{description}</div>}
      </div>
      <div className="flex min-w-0 flex-col gap-4">{children}</div>
    </section>
  )
}

/** A form on a panel: fields stacked, the actions in a footer row. */
export function FormPanel({
  children,
  footer,
  className,
  ...props
}: Omit<FormHTMLAttributes<HTMLFormElement>, 'children'> & {
  children: ReactNode
  footer: ReactNode
}) {
  return (
    <form className={cx('rounded-lg border border-line bg-surface', className)} {...props}>
      <div className="flex flex-col gap-4 p-4">{children}</div>
      <div className="flex flex-wrap items-center gap-3 rounded-b-lg border-t border-line-soft bg-bg px-4 py-2.5">
        {footer}
      </div>
    </form>
  )
}
