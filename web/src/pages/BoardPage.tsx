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
import { runStateTone } from '../runs/tones'
import { Button } from '../ui/Button'
import { cx } from '../ui/cx'
import { ErrorText } from '../ui/form'
import { StopIcon, TerminalIcon } from '../ui/icons'
import { EmptyState, Loading, PageBody, PageHeader } from '../ui/layout'
import { ConfirmDialog } from '../ui/overlay'
import { Tag } from '../ui/Tag'
import { usePageTitle } from '../ui/title'
import { AppShell } from './AppShell'

// State, ticket and step, branch, time, cost, actions; on narrow screens the row stacks.
const columns =
  'md:grid md:grid-cols-[10.5rem_minmax(0,1fr)_minmax(0,11rem)_4.5rem_8.5rem_10rem] md:items-start md:gap-x-4'

/** Every active run with its state, step, time, cost and live log. Updates live. */
export function BoardPage() {
  useWatch('WatchAll')
  const runs = useQuery(boardQuery)
  usePageTitle('Board')
  const now = useNow()
  const active = runs.data?.filter((run) => isActive(run.state)).length ?? 0

  return (
    <AppShell>
      <PageHeader
        title="Agent board"
        subtitle={
          runs.data &&
          `${active} active run${active === 1 ? '' : 's'} · runs that ended in the last hour stay listed`
        }
        wide
      />
      <PageBody wide>
        {runs.error && <ErrorText>{runs.error.message}</ErrorText>}
        {runs.isPending && <Loading />}
        {runs.data?.length === 0 && (
          <EmptyState title="No agent is working">
            Assign a ticket to an agent on its page.
          </EmptyState>
        )}
        {runs.data && runs.data.length > 0 && (
          <section
            aria-label="Runs"
            className="overflow-hidden rounded-lg border border-line bg-surface"
          >
            <div
              aria-hidden="true"
              className={cx(
                columns,
                'hidden border-b border-line px-4 py-2 text-xs font-medium text-fg-subtle',
              )}
            >
              <span>State</span>
              <span>Ticket and current step</span>
              <span>Branch</span>
              <span className="text-right">Time</span>
              <span className="text-right">Cost</span>
              <span />
            </div>
            {runs.data.map((run) => (
              <BoardRun key={run.id} run={run} now={now} />
            ))}
          </section>
        )}
      </PageBody>
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
  const resume = resumeLabel(run)

  return (
    <article
      className="border-t border-line-soft first:border-t-0"
      aria-label={`Run of ${run.ticketKey}`}
      data-state={run.state}
    >
      <div className={cx(columns, 'flex flex-col gap-2 px-4 py-3')}>
        <div className="pt-px">
          <Tag tone={runStateTone[run.state]} dot={active}>
            {runStateLabel[run.state]}
          </Tag>
        </div>
        <div className="min-w-0">
          <Link
            to="/projects/$slug/tickets/$number"
            params={{ slug: run.projectSlug, number: String(run.ticketNumber) }}
            className={cx(
              'flex items-baseline gap-2 hover:no-underline [&:hover_.title]:underline',
              active ? 'text-fg' : 'text-fg-2',
            )}
          >
            <span className="shrink-0 font-mono text-mono whitespace-nowrap text-fg-muted">
              {run.ticketKey}
            </span>
            <span className={cx('title', active ? 'font-semibold' : 'font-medium')}>
              {run.ticketTitle}
            </span>
          </Link>
          {run.progressStep && (
            <p className="m-0 mt-0.5 text-sm text-fg-2">
              {run.progressStep}
              {run.progressNote && <span className="text-fg-subtle"> ({run.progressNote})</span>}
            </p>
          )}
          {run.waiting.length > 0 && (
            <ul className="m-0 mt-1 list-none space-y-0.5 p-0 text-sm">
              {run.waiting.map((item) => (
                <li key={item.id} className="text-fg-2">
                  {waitingText(item)} – <Link to="/">answer in the inbox</Link>
                </li>
              ))}
            </ul>
          )}
          {run.failureReason && <p className="m-0 mt-1 text-sm text-bad">{run.failureReason}</p>}
          <p className="m-0 mt-0.5 text-sm text-fg-subtle">
            Claude Code · {run.model}
            {resume && ` · ${resume}`}
          </p>
        </div>
        <div
          className="truncate font-mono text-mono text-fg-muted md:pt-0.5"
          title={run.branch ?? undefined}
        >
          {run.branch}
        </div>
        <div
          className="font-mono text-mono text-fg-2 tabular-nums md:pt-0.5 md:text-right"
          title={active ? 'Running for' : 'Took'}
        >
          {elapsed(run, now)}
        </div>
        <div
          className="text-sm text-fg-muted tabular-nums md:pt-0.5 md:text-right"
          title="Cost as the agent reported it"
        >
          {costText(run)}
        </div>
        <div className="flex items-center gap-1 md:justify-end">
          <Button
            size="sm"
            variant={showLog ? 'secondary' : 'ghost'}
            aria-pressed={showLog}
            onClick={() => setShowLog(!showLog)}
          >
            <TerminalIcon size={14} />
            {showLog ? 'Hide log' : 'Live log'}
          </Button>
          {active && (
            <ConfirmDialog
              trigger={
                <Button
                  size="sm"
                  variant="danger"
                  disabled={cancel.isPending || run.cancelRequested}
                >
                  <StopIcon size={13} />
                  {run.cancelRequested ? 'Stopping…' : 'Stop'}
                </Button>
              }
              title={`Stop the agent on ${run.ticketKey}?`}
              description="The run is cancelled and its container stopped. Work that was not submitted is lost."
              confirmLabel="Stop and cancel"
              cancelLabel="Keep running"
              danger
              onConfirm={() => cancel.mutate(run.id)}
            />
          )}
        </div>
      </div>
      {cancel.error && <ErrorText className="px-4 pb-3">{cancel.error.message}</ErrorText>}
      {showLog && (
        <div className="px-4 pb-4">
          <RunLog runId={run.id} />
        </div>
      )}
    </article>
  )
}
