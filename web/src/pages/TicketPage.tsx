import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { useState } from 'react'
import { MarkdownView } from '../tickets/MarkdownView'
import {
  priorityLabel,
  statusLabel,
  ticketQuery,
  toTicketForm,
  toUpdateTicketRequest,
  useDeleteTicket,
  useUpdateTicket,
} from '../tickets/tickets'
import { AppShell } from './AppShell'
import { TicketFormView } from './TicketForm'

export function TicketPage({ slug, number }: { slug: string; number: number }) {
  const ticket = useQuery(ticketQuery(slug, number))
  const update = useUpdateTicket(slug, number)
  const remove = useDeleteTicket(slug, number)
  const navigate = useNavigate()
  const [editing, setEditing] = useState(false)
  const t = ticket.data

  return (
    <AppShell>
      <p className="muted">
        <Link to="/projects/$slug/tickets" params={{ slug }}>
          All tickets
        </Link>
      </p>
      {ticket.error && (
        <p role="alert" className="error">
          {ticket.error.message}
        </p>
      )}
      {!t && !ticket.error && <p>Loading…</p>}
      {t && editing && (
        <>
          <h1>
            <span className="muted mono">{t.key}</span> Edit
          </h1>
          <TicketFormView
            initial={toTicketForm(t)}
            withStatus
            submitLabel="Save"
            pending={update.isPending}
            error={update.error}
            onSubmit={(form) =>
              update.mutate(toUpdateTicketRequest(form), { onSuccess: () => setEditing(false) })
            }
            onCancel={() => {
              update.reset()
              setEditing(false)
            }}
          />
        </>
      )}
      {t && !editing && (
        <article className="stack">
          <h1>
            <span className="muted mono">{t.key}</span> {t.title}
          </h1>
          <p className="row">
            <span className={`badge ${t.status}`}>{statusLabel[t.status]}</span>
            <span className="muted">Priority {priorityLabel[t.priority].toLowerCase()}</span>
            <span className="muted">Updated {new Date(t.updatedAt).toLocaleString()}</span>
          </p>
          {t.description.trim() ? (
            <MarkdownView text={t.description} />
          ) : (
            <p className="muted">No description.</p>
          )}
          <h2>Acceptance criteria</h2>
          {t.criteria.length === 0 ? (
            <p className="muted">None yet.</p>
          ) : (
            <ol className="list">
              {t.criteria.map((c) => (
                <li key={c.id}>{c.text}</li>
              ))}
            </ol>
          )}
          <div className="row">
            <button type="button" onClick={() => setEditing(true)}>
              Edit
            </button>
            <button
              type="button"
              className="danger"
              disabled={remove.isPending}
              onClick={() => {
                if (window.confirm(`Delete ${t.key}? Its number is not given out again.`)) {
                  remove.mutate(undefined, {
                    onSuccess: () => navigate({ to: '/projects/$slug/tickets', params: { slug } }),
                  })
                }
              }}
            >
              Delete
            </button>
            {remove.error && <span className="error">{remove.error.message}</span>}
          </div>
        </article>
      )}
    </AppShell>
  )
}
