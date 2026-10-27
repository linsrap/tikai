import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { useEffect, useState } from 'react'
import { useWatch } from '../live/live'
import { RunLog } from '../live/RunLog'
import {
  boardQuery,
  costText,
  elapsed,
  isActive,
  resumeLabel,
  runStateLabel,
  useCancelRun,
  waitingText,
  type RunInfo,
} from '../runs/runs'
import { AppShell } from './AppShell'

/** Every active run with its state, step, time, cost and live log. Updates live. */
export function BoardPage() {
  useWatch('WatchAll')
  const runs = useQuery(boardQuery)
  const now = useNow()

  return (
    <AppShell>
      <h1>Agent board</h1>
      {runs.error && <p className="error">{runs.error.message}</p>}
      {runs.isPending && <p>Loading…</p>}
      {runs.data?.length === 0 && (
        <p className="muted">No agent is working. Assign a ticket to an agent on its page.</p>
      )}
      <section className="stack" aria-label="Runs">
        {runs.data?.map((run) => (
          <BoardRun key={run.id} run={run} now={now} />
        ))}
      </section>
    </AppShell>
  )
}

/** The current time, every second, for elapsed times. */
function useNow() {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [])
  return now
}

function BoardRun({ run, now }: { run: RunInfo; now: number }) {
  const [showLog, setShowLog] = useState(false)
  const cancel = useCancelRun(run.projectSlug)
  const active = isActive(run.state)

  return (
    <article
      className={`card stack${active ? '' : ' ended'}`}
      aria-label={`Run of ${run.ticketKey}`}
      data-state={run.state}
    >
      <header className="row spread">
        <span className="row">
          <span className={`badge ${run.state}`}>{runStateLabel[run.state]}</span>
          <Link
            to="/projects/$slug/tickets/$number"
            params={{ slug: run.projectSlug, number: String(run.ticketNumber) }}
          >
            <strong>{run.ticketKey}</strong> {run.ticketTitle}
          </Link>
        </span>
        <span className="mono" title={active ? 'Running for' : 'Took'}>
          {elapsed(run, now)}
        </span>
      </header>
      {run.progressStep && (
        <p>
          {run.progressStep}
          {run.progressNote && <span className="muted"> ({run.progressNote})</span>}
        </p>
      )}
      {run.waiting.length > 0 && (
        <ul className="list">
          {run.waiting.map((item) => (
            <li key={item.id}>
              {waitingText(item)} – <Link to="/">answer in the inbox</Link>
            </li>
          ))}
        </ul>
      )}
      {run.failureReason && <p className="error">{run.failureReason}</p>}
      <p className="muted">
        Claude Code · {run.model} · <span className="mono">{run.branch}</span> · {costText(run)}
        {resumeLabel(run) && ` · ${resumeLabel(run)}`}
      </p>
      <div className="row">
        <button type="button" aria-pressed={showLog} onClick={() => setShowLog(!showLog)}>
          {showLog ? 'Hide log' : 'Live log'}
        </button>
        {active && (
          <button
            type="button"
            className="danger"
            disabled={cancel.isPending || run.cancelRequested}
            onClick={() => {
              if (window.confirm(`Stop the agent on ${run.ticketKey} and cancel this run?`)) {
                cancel.mutate(run.id)
              }
            }}
          >
            {run.cancelRequested ? 'Stopping…' : 'Stop'}
          </button>
        )}
        {cancel.error && <span className="error">{cancel.error.message}</span>}
      </div>
      {showLog && <RunLog runId={run.id} />}
    </article>
  )
}
