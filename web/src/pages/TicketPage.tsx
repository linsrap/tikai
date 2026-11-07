import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { useState, type ReactNode } from 'react'
import { projectQuery } from '../projects/projects'
import { MarkdownView } from '../tickets/MarkdownView'
import {
  statusLabel,
  ticketQuery,
  toTicketForm,
  toUpdateTicketRequest,
  useDeleteTicket,
  useUpdateTicket,
} from '../tickets/tickets'
import { ticketStatusTone } from '../tickets/tones'
import { Button } from '../ui/Button'
import { ErrorText, Hint } from '../ui/form'
import { CrumbSep, Loading, PageBody, PageHeader } from '../ui/layout'
import { ConfirmDialog } from '../ui/overlay'
import { Tag } from '../ui/Tag'
import { AppShell } from './AppShell'
import { RunsSection } from './RunsSection'
import { TicketFormView } from './TicketForm'
import { Priority } from './TicketsPage'

export function TicketPage({ slug, number }: { slug: string; number: number }) {
  const ticket = useQuery(ticketQuery(slug, number))
  const project = useQuery(projectQuery(slug))
  const update = useUpdateTicket(slug, number)
  const remove = useDeleteTicket(slug, number)
  const navigate = useNavigate()
  const [editing, setEditing] = useState(false)
  const t = ticket.data

  const crumbs = (
    <>
      <Link to="/projects/$slug/tickets" params={{ slug }}>
        {project.data?.name ?? slug}
      </Link>
      <CrumbSep />
      <span className="font-mono text-mono">{t?.key ?? `#${number}`}</span>
    </>
  )

  return (
    <AppShell>
      {ticket.error && (
        <PageBody>
          <ErrorText>{ticket.error.message}</ErrorText>
        </PageBody>
      )}
      {!t && !ticket.error && (
        <PageBody>
          <Loading />
        </PageBody>
      )}
      {t && editing && (
        <>
          <PageHeader crumbs={crumbs} title={`Edit ${t.key}`} />
          <PageBody>
            <TicketFormView
              slug={slug}
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
          </PageBody>
        </>
      )}
      {t && !editing && (
        <>
          <PageHeader
            crumbs={crumbs}
            title={t.title}
            wide
            actions={
              <>
                <Button onClick={() => setEditing(true)}>Edit</Button>
                <ConfirmDialog
                  trigger={
                    <Button variant="danger" disabled={remove.isPending}>
                      Delete
                    </Button>
                  }
                  title={`Delete ${t.key}?`}
                  description="Its number is not given out again."
                  confirmLabel="Delete ticket"
                  danger
                  onConfirm={() =>
                    remove.mutate(undefined, {
                      onSuccess: () =>
                        navigate({ to: '/projects/$slug/tickets', params: { slug } }),
                    })
                  }
                />
              </>
            }
          />
          <PageBody wide>
            {remove.error && <ErrorText className="mb-4">{remove.error.message}</ErrorText>}
            <div className="grid gap-x-10 gap-y-8 lg:grid-cols-[minmax(0,1fr)_16rem]">
              <article className="flex min-w-0 flex-col gap-8">
                <div className="rounded-lg border border-line bg-surface px-5 py-4">
                  {t.description.trim() ? (
                    <MarkdownView text={t.description} />
                  ) : (
                    <Hint>No description.</Hint>
                  )}
                </div>
                <section aria-labelledby="criteria">
                  <h2 id="criteria" className="m-0 mb-2.5 text-sm font-semibold">
                    Acceptance criteria
                  </h2>
                  {t.criteria.length === 0 ? (
                    <Hint>None yet.</Hint>
                  ) : (
                    <ol className="m-0 list-none rounded-lg border border-line bg-surface p-0">
                      {t.criteria.map((c, i) => (
                        <li
                          key={c.id}
                          className="flex gap-3 border-t border-line-soft px-4 py-2.5 first:border-t-0"
                        >
                          <span className="w-5 shrink-0 text-right text-sm text-fg-subtle tabular-nums">
                            {i + 1}.
                          </span>
                          <span>{c.text}</span>
                        </li>
                      ))}
                    </ol>
                  )}
                </section>
                <RunsSection slug={slug} number={number} status={t.status} />
              </article>
              <aside className="lg:order-last" aria-label="Details">
                <dl className="m-0 flex flex-col gap-4 lg:sticky lg:top-6">
                  <Detail label="Status">
                    <Tag tone={ticketStatusTone[t.status]}>{statusLabel[t.status]}</Tag>
                  </Detail>
                  <Detail label="Priority">
                    <Priority priority={t.priority} />
                  </Detail>
                  <Detail label="Key">
                    <span className="font-mono text-mono">{t.key}</span>
                  </Detail>
                  <Detail label="Updated">
                    <span className="text-sm text-fg-2">
                      {new Date(t.updatedAt).toLocaleString()}
                    </span>
                  </Detail>
                </dl>
              </aside>
            </div>
          </PageBody>
        </>
      )}
    </AppShell>
  )
}

function Detail({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <dt className="text-xs font-medium text-fg-subtle">{label}</dt>
      <dd className="m-0">{children}</dd>
    </div>
  )
}
