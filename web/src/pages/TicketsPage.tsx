import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { useState } from 'react'
import { projectQuery } from '../projects/projects'
import {
  emptyTicketForm,
  priorityLabel,
  statusLabel,
  ticketFilters,
  ticketsQuery,
  toCreateTicketRequest,
  useCreateTicket,
  type TicketFilter,
  type TicketPriority,
} from '../tickets/tickets'
import { ticketStatusTone } from '../tickets/tones'
import { Button } from '../ui/Button'
import { cx } from '../ui/cx'
import { ErrorText } from '../ui/form'
import { PlusIcon } from '../ui/icons'
import { CrumbSep, EmptyState, Loading, PageBody, PageHeader } from '../ui/layout'
import { table } from '../ui/table'
import { Segmented } from '../ui/Segmented'
import { Dot } from '../ui/Tag'
import { usePageTitle } from '../ui/title'
import { AppShell } from './AppShell'
import { ProjectHeader } from './ProjectHeader'
import { TicketFormView } from './TicketForm'

const filterLabel: Record<TicketFilter, string> = {
  active: 'Active',
  done: 'Done',
  closed: 'Closed',
  all: 'All',
}

export function TicketsPage({ slug }: { slug: string }) {
  const tickets = useQuery(ticketsQuery(slug))
  const project = useQuery(projectQuery(slug))
  usePageTitle(`Tickets · ${project.data?.name ?? slug}`)
  const [filter, setFilter] = useState<TicketFilter>('active')
  const inFilter = (f: TicketFilter) =>
    tickets.data?.filter((t) => (ticketFilters[f] as string[]).includes(t.status))
  const shown = inFilter(filter)

  return (
    <AppShell>
      <ProjectHeader
        slug={slug}
        actions={
          <Button variant="primary" asChild>
            <Link to="/projects/$slug/tickets/new" params={{ slug }}>
              <PlusIcon size={14} strokeWidth={1.8} />
              New ticket
            </Link>
          </Button>
        }
      />
      <PageBody>
        <div className="mb-3">
          <Segmented
            label="Filter"
            value={filter}
            onChange={setFilter}
            options={(Object.keys(filterLabel) as TicketFilter[]).map((f) => ({
              value: f,
              label: filterLabel[f],
              count: inFilter(f)?.length,
            }))}
          />
        </div>
        {tickets.isPending && <Loading />}
        {tickets.error && <ErrorText>{tickets.error.message}</ErrorText>}
        {shown?.length === 0 && (
          <EmptyState title="No tickets here">
            {filter === 'active' ? 'Create one with “New ticket”.' : 'Try another filter.'}
          </EmptyState>
        )}
        {shown && shown.length > 0 && (
          <div className={table.wrap}>
            <table className={cx(table.table, 'min-w-[40rem]')}>
              <thead>
                <tr>
                  <th className={cx(table.th, 'w-24')}>Key</th>
                  <th className={table.th}>Title</th>
                  <th className={cx(table.th, 'w-36')}>Status</th>
                  <th className={cx(table.th, 'w-28')}>Priority</th>
                  <th className={cx(table.th, 'w-28')}>Criteria</th>
                </tr>
              </thead>
              <tbody>
                {shown.map((t) => (
                  <tr key={t.id} className={table.tr}>
                    <td className={cx(table.td, 'font-mono text-mono text-fg-muted')}>
                      <Link
                        to="/projects/$slug/tickets/$number"
                        params={{ slug, number: String(t.number) }}
                        className="text-fg-muted"
                        tabIndex={-1}
                      >
                        {t.key}
                      </Link>
                    </td>
                    <td className={table.td}>
                      <Link
                        to="/projects/$slug/tickets/$number"
                        params={{ slug, number: String(t.number) }}
                        className="font-medium text-fg"
                      >
                        {t.title}
                      </Link>
                    </td>
                    <td className={cx(table.td, 'whitespace-nowrap')}>
                      <span className="inline-flex items-center gap-2 text-sm">
                        <Dot tone={ticketStatusTone[t.status]} />
                        {statusLabel[t.status]}
                      </span>
                    </td>
                    <td className={cx(table.td, 'whitespace-nowrap')}>
                      <Priority priority={t.priority} />
                    </td>
                    <td className={cx(table.td, 'text-sm whitespace-nowrap text-fg-subtle')}>
                      {t.criteriaCount === 0
                        ? 'No criteria'
                        : `${t.criteriaCount} ${t.criteriaCount === 1 ? 'criterion' : 'criteria'}`}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </PageBody>
    </AppShell>
  )
}

const priorityBars: Record<TicketPriority, number> = { low: 1, normal: 2, high: 3, urgent: 3 }

/** Priority as three bars and the word; urgent in red. */
export function Priority({ priority }: { priority: TicketPriority }) {
  const filled = priorityBars[priority]
  return (
    <span className="inline-flex items-center gap-2 text-sm text-fg-muted">
      <span aria-hidden="true" className="inline-flex h-3 items-end gap-0.5">
        {[5, 8, 11].map((height, i) => (
          <span
            key={height}
            style={{ height }}
            className={cx(
              'w-[3px] rounded-[1px]',
              i >= filled
                ? 'bg-line-strong'
                : priority === 'urgent'
                  ? 'bg-bad-solid'
                  : 'bg-fg-muted',
            )}
          />
        ))}
      </span>
      {priorityLabel[priority]}
    </span>
  )
}

export function NewTicketPage({ slug }: { slug: string }) {
  const create = useCreateTicket(slug)
  const navigate = useNavigate()
  const project = useQuery(projectQuery(slug))
  usePageTitle(`New ticket · ${project.data?.name ?? slug}`)

  return (
    <AppShell>
      <PageHeader
        crumbs={
          <>
            <Link to="/projects/$slug/tickets" params={{ slug }}>
              {project.data?.name ?? slug}
            </Link>
            <CrumbSep />
            <span>New ticket</span>
          </>
        }
        title="New ticket"
      />
      <PageBody>
        <TicketFormView
          slug={slug}
          initial={emptyTicketForm()}
          withStatus={false}
          submitLabel="Create ticket"
          pending={create.isPending}
          error={create.error}
          onSubmit={(form) =>
            create.mutate(toCreateTicketRequest(form), {
              onSuccess: (t) =>
                navigate({
                  to: '/projects/$slug/tickets/$number',
                  params: { slug, number: String(t.number) },
                }),
            })
          }
          onCancel={() => navigate({ to: '/projects/$slug/tickets', params: { slug } })}
        />
      </PageBody>
    </AppShell>
  )
}
