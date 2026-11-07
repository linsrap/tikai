import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { useState } from 'react'
import { PlanAnswer, QuestionAnswer } from '../inbox/AnswerForms'
import { useWatch } from '../live/live'
import { RunLog } from '../live/RunLog'
import { providersQuery } from '../llm/llm'
import { repositoriesQuery } from '../projects/projects'
import {
  blockedNote,
  costText,
  isActive,
  resumeLabel,
  runsQuery,
  runStateLabel,
  toStartRunRequest,
  useCancelRun,
  useStartRun,
  waitingText,
  type RunInfo,
} from '../runs/runs'
import { runStateTone } from '../runs/tones'
import type { TicketStatus } from '../tickets/tickets'
import { Button } from '../ui/Button'
import { ErrorText, Hint, Select } from '../ui/form'
import { TerminalIcon } from '../ui/icons'
import { ConfirmDialog } from '../ui/overlay'
import { Tag } from '../ui/Tag'

/** "Assign to agent" and the ticket's runs, live (the page watches its project). */
export function RunsSection({
  slug,
  number,
  status,
}: {
  slug: string
  number: number
  status: TicketStatus
}) {
  useWatch('WatchProject', slug)
  const runs = useQuery(runsQuery(slug, number))
  const active = runs.data?.find((r) => isActive(r.state))

  return (
    <section className="flex flex-col gap-3" aria-label="Agent runs">
      <h2 className="m-0 text-sm font-semibold">Agent</h2>
      {!active && (status === 'open' || status === 'in_progress') && (
        <AssignBox slug={slug} number={number} />
      )}
      {runs.error && <ErrorText>{runs.error.message}</ErrorText>}
      {runs.data?.length === 0 && <Hint>No runs yet.</Hint>}
      {runs.data && runs.data.length > 0 && (
        <div className="rounded-lg border border-line bg-surface">
          {runs.data.map((run) => (
            <RunCard key={run.id} slug={slug} run={run} />
          ))}
        </div>
      )}
    </section>
  )
}

function AssignBox({ slug, number }: { slug: string; number: number }) {
  const repositories = useQuery(repositoriesQuery(slug))
  const projectProviders = useQuery(providersQuery({ kind: 'project', slug }))
  const ownProviders = useQuery(providersQuery({ kind: 'user' }))
  // The server prefers the project's providers too; this list shows them first.
  const providers = [...(projectProviders.data ?? []), ...(ownProviders.data ?? [])]
  const [repositoryId, setRepositoryId] = useState<string | null>(null)
  const [providerId, setProviderId] = useState<string | null>(null)
  const start = useStartRun(slug, number)

  if (repositories.isPending || projectProviders.isPending || ownProviders.isPending) {
    return null
  }
  const repos = repositories.data ?? []
  if (repos.length === 0 || providers.length === 0) {
    return (
      <p className="m-0 rounded-lg border border-dashed border-line-strong px-4 py-3 text-sm text-fg-muted">
        To assign this ticket to an agent, add{' '}
        {repos.length === 0 ? 'a repository' : 'an LLM provider (the agent uses its API key)'} to
        the{' '}
        <Link to="/projects/$slug" params={{ slug }}>
          project
        </Link>
        {repos.length > 0 && (
          <>
            {' '}
            or your own under <Link to="/settings">Settings</Link>
          </>
        )}
        .
      </p>
    )
  }

  return (
    <div className="flex flex-col gap-3 rounded-lg border border-line bg-surface p-4">
      <div className="flex flex-wrap items-center gap-3">
        <Button
          variant="primary"
          disabled={start.isPending || (repos.length > 1 && !repositoryId)}
          onClick={() => start.mutate(toStartRunRequest(repositoryId, providerId))}
        >
          {start.isPending ? 'Assigning…' : 'Assign to agent (Claude Code)'}
        </Button>
        {repos.length > 1 && (
          <label className="flex items-center gap-2 text-sm text-fg-muted">
            <span>in</span>
            <Select
              value={repositoryId ?? ''}
              onChange={(e) => setRepositoryId(e.target.value || null)}
            >
              <option value="">Choose a repository</option>
              {repos.map((r) => (
                <option key={r.id} value={r.id}>
                  {r.name}
                </option>
              ))}
            </Select>
          </label>
        )}
        {providers.length > 1 && (
          <label className="flex items-center gap-2 text-sm text-fg-muted">
            <span>using</span>
            <Select
              value={providerId ?? ''}
              onChange={(e) => setProviderId(e.target.value || null)}
            >
              <option value="">{providers[0].name} (default)</option>
              {providers.slice(1).map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </Select>
          </label>
        )}
      </div>
      <Hint>
        The agent proposes a plan first and waits until it is approved here or in the{' '}
        <Link to="/">inbox</Link>. Its API usage is billed to the provider&apos;s key.
      </Hint>
      {start.error && <ErrorText>{start.error.message}</ErrorText>}
    </div>
  )
}

const time = (iso: string) => new Date(iso).toLocaleString()

/** What a blocked run waits for, answered right here (or in the inbox). */
function Waiting({ run }: { run: RunInfo }) {
  return (
    <div className="flex flex-col gap-3 rounded-md border border-wait-bg bg-bg p-3">
      {run.blockedSince && (
        <p className="m-0 text-sm font-medium text-wait">
          Waiting for an answer since {time(run.blockedSince)}
        </p>
      )}
      {run.waiting.map((item) =>
        item.plan ? (
          <PlanAnswer key={item.id} id={item.id} plan={item.plan} />
        ) : item.question ? (
          <QuestionAnswer key={item.id} id={item.id} question={item.question} />
        ) : (
          <p key={item.id} className="m-0">
            {waitingText(item)}
          </p>
        ),
      )}
      {blockedNote(run, time) && <Hint>{blockedNote(run, time)}</Hint>}
    </div>
  )
}

function RunCard({ slug, run }: { slug: string; run: RunInfo }) {
  const cancel = useCancelRun(slug)
  const [showEvents, setShowEvents] = useState(false)
  const resume = resumeLabel(run)

  return (
    <article className="flex flex-col gap-3 border-t border-line-soft px-4 py-3.5 first:border-t-0">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <Tag tone={runStateTone[run.state]} dot={isActive(run.state)}>
          {runStateLabel[run.state]}
        </Tag>
        <span className="font-mono text-mono text-fg-muted">{run.branch}</span>
        <span className="text-sm text-fg-subtle">
          {run.model} · {run.repositoryName} · started {new Date(run.createdAt).toLocaleString()}
        </span>
      </div>
      {run.state === 'blocked' && <Waiting run={run} />}
      {resume && <Hint>{resume}</Hint>}
      {run.progressStep && (
        <p className="m-0">
          {run.progressStep}
          {run.progressNote && <span className="text-fg-subtle"> ({run.progressNote})</span>}
        </p>
      )}
      {run.failureReason && <p className="m-0 text-bad">{run.failureReason}</p>}
      <div className="flex flex-wrap items-center gap-2">
        {run.changeSetId && (
          <Button variant="primary" size="sm" asChild>
            <Link to="/reviews/$id" params={{ id: run.changeSetId }}>
              Review
            </Link>
          </Button>
        )}
        <Button
          size="sm"
          variant={showEvents ? 'secondary' : 'ghost'}
          onClick={() => setShowEvents(!showEvents)}
          aria-pressed={showEvents}
        >
          <TerminalIcon size={14} />
          Transcript
        </Button>
        {isActive(run.state) && (
          <ConfirmDialog
            trigger={
              <Button size="sm" variant="danger" disabled={cancel.isPending || run.cancelRequested}>
                {run.cancelRequested ? 'Cancelling…' : 'Cancel run'}
              </Button>
            }
            title="Stop the agent and cancel this run?"
            description="Its commits stay in the run directory."
            confirmLabel="Cancel run"
            cancelLabel="Keep running"
            danger
            onConfirm={() => cancel.mutate(run.id)}
          />
        )}
        <span className="ml-auto text-sm text-fg-subtle tabular-nums">Cost: {costText(run)}</span>
      </div>
      {cancel.error && <ErrorText>{cancel.error.message}</ErrorText>}
      {showEvents && <RunLog runId={run.id} />}
    </article>
  )
}
