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
import type { TicketStatus } from '../tickets/tickets'

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
    <section className="stack" aria-label="Agent runs">
      <h2>Agent</h2>
      {!active && (status === 'open' || status === 'in_progress') && (
        <AssignBox slug={slug} number={number} />
      )}
      {runs.error && <p className="error">{runs.error.message}</p>}
      {runs.data?.length === 0 && <p className="muted">No runs yet.</p>}
      {runs.data?.map((run) => (
        <RunCard key={run.id} slug={slug} run={run} />
      ))}
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
      <p className="muted">
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
    <div className="card stack">
      <div className="row">
        <button
          type="button"
          disabled={start.isPending || (repos.length > 1 && !repositoryId)}
          onClick={() => start.mutate(toStartRunRequest(repositoryId, providerId))}
        >
          {start.isPending ? 'Assigning…' : 'Assign to agent (Claude Code)'}
        </button>
        {repos.length > 1 && (
          <label className="row">
            <span className="muted">in</span>
            <select
              value={repositoryId ?? ''}
              onChange={(e) => setRepositoryId(e.target.value || null)}
            >
              <option value="">Choose a repository</option>
              {repos.map((r) => (
                <option key={r.id} value={r.id}>
                  {r.name}
                </option>
              ))}
            </select>
          </label>
        )}
        {providers.length > 1 && (
          <label className="row">
            <span className="muted">using</span>
            <select
              value={providerId ?? ''}
              onChange={(e) => setProviderId(e.target.value || null)}
            >
              <option value="">{providers[0].name} (default)</option>
              {providers.slice(1).map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </label>
        )}
      </div>
      <p className="muted">
        The agent proposes a plan first and waits until it is approved here or in the{' '}
        <Link to="/">inbox</Link>. Its API usage is billed to the provider&apos;s key.
      </p>
      {start.error && <p className="error">{start.error.message}</p>}
    </div>
  )
}

const time = (iso: string) => new Date(iso).toLocaleString()

/** What a blocked run waits for, answered right here (or in the inbox). */
function Waiting({ run }: { run: RunInfo }) {
  return (
    <div className="stack">
      {run.blockedSince && <p>Waiting for an answer since {time(run.blockedSince)}</p>}
      {run.waiting.map((item) =>
        item.plan ? (
          <PlanAnswer key={item.id} id={item.id} plan={item.plan} />
        ) : item.question ? (
          <QuestionAnswer key={item.id} id={item.id} question={item.question} />
        ) : (
          <p key={item.id}>{waitingText(item)}</p>
        ),
      )}
      {blockedNote(run, time) && <p className="muted">{blockedNote(run, time)}</p>}
    </div>
  )
}

function RunCard({ slug, run }: { slug: string; run: RunInfo }) {
  const cancel = useCancelRun(slug)
  const [showEvents, setShowEvents] = useState(false)

  return (
    <article className="card stack">
      <p className="row">
        <span className={`badge ${run.state}`}>{runStateLabel[run.state]}</span>
        <span className="mono">{run.branch}</span>
        <span className="muted">
          {run.model} · {run.repositoryName} · started {new Date(run.createdAt).toLocaleString()}
        </span>
      </p>
      {run.state === 'blocked' && <Waiting run={run} />}
      {resumeLabel(run) && <p className="muted">{resumeLabel(run)}</p>}
      {run.progressStep && (
        <p>
          {run.progressStep}
          {run.progressNote && <span className="muted"> ({run.progressNote})</span>}
        </p>
      )}
      {run.failureReason && <p className="error">{run.failureReason}</p>}
      <p className="muted">Cost: {costText(run)}</p>
      <div className="row">
        {run.changeSetId && (
          <Link to="/reviews/$id" params={{ id: run.changeSetId }} className="button primary">
            Review
          </Link>
        )}
        <button type="button" onClick={() => setShowEvents(!showEvents)} aria-pressed={showEvents}>
          Transcript
        </button>
        {isActive(run.state) && (
          <button
            type="button"
            className="danger"
            disabled={cancel.isPending || run.cancelRequested}
            onClick={() => {
              if (
                window.confirm(
                  'Stop the agent and cancel this run? Its commits stay in the run directory.',
                )
              ) {
                cancel.mutate(run.id)
              }
            }}
          >
            {run.cancelRequested ? 'Cancelling…' : 'Cancel run'}
          </button>
        )}
        {cancel.error && <span className="error">{cancel.error.message}</span>}
      </div>
      {showEvents && <RunLog runId={run.id} />}
    </article>
  )
}
