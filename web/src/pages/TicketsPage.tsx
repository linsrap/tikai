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
} from '../tickets/tickets'
import { AppShell } from './AppShell'
import { TicketFormView } from './TicketForm'

const filterLabel: Record<TicketFilter, string> = {
  active: 'Active',
  done: 'Done',
  closed: 'Closed',
  all: 'All',
}

export function TicketsPage({ slug }: { slug: string }) {
  const project = useQuery(projectQuery(slug))
  const tickets = useQuery(ticketsQuery(slug))
  const [filter, setFilter] = useState<TicketFilter>('active')
  const shown = tickets.data?.filter((t) => (ticketFilters[filter] as string[]).includes(t.status))

  return (
    <AppShell>
      <p className="muted">
        <Link to="/projects/$slug" params={{ slug }}>
          {project.data?.name ?? slug}
        </Link>
      </p>
      <div className="row spread">
        <h1>Tickets</h1>
        <Link to="/projects/$slug/tickets/new" params={{ slug }}>
          New ticket
        </Link>
      </div>
      <div className="row" role="group" aria-label="Filter">
        {(Object.keys(filterLabel) as TicketFilter[]).map((f) => (
          <button key={f} type="button" aria-pressed={filter === f} onClick={() => setFilter(f)}>
            {filterLabel[f]}
          </button>
        ))}
      </div>
      {tickets.isPending && <p>Loading…</p>}
      {(tickets.error ?? project.error) && (
        <p role="alert" className="error">
          {(tickets.error ?? project.error)?.message}
        </p>
      )}
      {shown?.length === 0 && <p>No tickets here.</p>}
      {shown && shown.length > 0 && (
        <table className="tickets">
          <tbody>
            {shown.map((t) => (
              <tr key={t.id}>
                <td className="mono">
                  <Link
                    to="/projects/$slug/tickets/$number"
                    params={{ slug, number: String(t.number) }}
                  >
                    {t.key}
                  </Link>
                </td>
                <td>
                  <Link
                    to="/projects/$slug/tickets/$number"
                    params={{ slug, number: String(t.number) }}
                  >
                    {t.title}
                  </Link>
                </td>
                <td>
                  <span className={`badge ${t.status}`}>{statusLabel[t.status]}</span>
                </td>
                <td className="muted">{priorityLabel[t.priority]}</td>
                <td className="muted">
                  {t.criteriaCount} {t.criteriaCount === 1 ? 'criterion' : 'criteria'}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </AppShell>
  )
}

export function NewTicketPage({ slug }: { slug: string }) {
  const create = useCreateTicket(slug)
  const navigate = useNavigate()
  const project = useQuery(projectQuery(slug))

  return (
    <AppShell>
      <p className="muted">
        <Link to="/projects/$slug/tickets" params={{ slug }}>
          {project.data?.name ?? slug} tickets
        </Link>
      </p>
      <h1>New ticket</h1>
      <TicketFormView
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
    </AppShell>
  )
}
