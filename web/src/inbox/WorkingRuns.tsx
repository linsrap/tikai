import { Link } from '@tanstack/react-router'
import { RunLog } from '../live/RunLog'
import { elapsed, runStateLabel, type RunInfo } from '../runs/runs'
import { runStateTone } from '../runs/tones'
import { Button } from '../ui/Button'
import { useNow } from '../runs/useNow'
import { TerminalIcon, XIcon } from '../ui/icons'
import { SectionLabel } from '../ui/layout'
import { Dot, Tag } from '../ui/Tag'
import { continuedLine } from './answered'

/**
 * The agents at work, on the inbox page: state, ticket, current step and time per run, and whether a run took up an
 * answer sent from here. The log of one run opens beside the list (wide screens) or under its row (`inline`).
 */
export function WorkingRuns({
  runs,
  openLog,
  onToggleLog,
  inline,
  answered,
}: {
  runs: RunInfo[]
  openLog: string | undefined
  onToggleLog: (runId: string) => void
  inline: boolean
  answered: ReadonlyMap<string, number>
}) {
  // The clock ticks here, not in the inbox page, so its items do not render every second.
  const now = useNow()
  if (runs.length === 0) return null
  return (
    <section aria-label="Agents working" className="mb-6">
      <SectionLabel>
        <Dot tone="run" />
        Agents working
      </SectionLabel>
      <div className="rounded-lg border border-line bg-surface">
        {runs.map((run) => (
          <WorkingRun
            key={run.id}
            run={run}
            logOpen={openLog === run.id}
            onToggleLog={() => onToggleLog(run.id)}
            inline={inline}
            continued={continuedLine(run, answered.get(run.id), now)}
            now={now}
          />
        ))}
      </div>
    </section>
  )
}

function WorkingRun({
  run,
  logOpen,
  onToggleLog,
  inline,
  continued,
  now,
}: {
  run: RunInfo
  logOpen: boolean
  onToggleLog: () => void
  inline: boolean
  continued: string | null
  now: number
}) {
  return (
    <article
      className="border-t border-line-soft px-4 py-2.5 first:border-t-0"
      aria-label={`Run of ${run.ticketKey}`}
      data-state={run.state}
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <Tag tone={runStateTone[run.state]} dot>
          {runStateLabel[run.state]}
        </Tag>
        <Link
          to="/projects/$slug/tickets/$number"
          params={{ slug: run.projectSlug, number: String(run.ticketNumber) }}
          className="flex min-w-0 flex-1 items-baseline gap-2 text-fg hover:no-underline [&:hover_.title]:underline"
        >
          <span className="shrink-0 font-mono text-mono text-fg-muted">{run.ticketKey}</span>
          <span className="title truncate font-medium">{run.ticketTitle}</span>
        </Link>
        <span className="font-mono text-mono text-fg-2 tabular-nums" title="Running for">
          {elapsed(run, now)}
        </span>
        <Button
          size="sm"
          variant={logOpen ? 'secondary' : 'ghost'}
          aria-pressed={logOpen}
          onClick={onToggleLog}
        >
          <TerminalIcon size={14} />
          Log
        </Button>
      </div>
      {(run.progressStep || continued) && (
        <p className="m-0 mt-1 flex flex-wrap gap-x-2 text-sm">
          {continued && <span className="font-medium text-run">{continued}</span>}
          {run.progressStep && (
            <span className="min-w-0 truncate text-fg-2">
              {run.progressStep}
              {run.progressNote && <span className="text-fg-subtle"> ({run.progressNote})</span>}
            </span>
          )}
        </p>
      )}
      {inline && logOpen && (
        <div className="mt-2">
          <RunLog runId={run.id} />
        </div>
      )}
    </article>
  )
}

/** The open run's live log beside the inbox, as tall as the window. */
export function LogPanel({ run, onClose }: { run: RunInfo; onClose: () => void }) {
  return (
    <aside
      aria-label={`Live log of ${run.ticketKey}`}
      className="sticky top-4 flex flex-col gap-2 self-start"
    >
      <div className="flex items-center gap-2.5">
        <span className="font-mono text-mono text-fg-muted">{run.ticketKey}</span>
        <span className="min-w-0 flex-1 truncate font-medium text-fg">{run.ticketTitle}</span>
        <Tag tone={runStateTone[run.state]}>{runStateLabel[run.state]}</Tag>
        <Button size="sm" variant="ghost" aria-label="Close the log" onClick={onClose}>
          <XIcon size={13} />
        </Button>
      </div>
      <RunLog runId={run.id} height="h-[calc(100vh-11rem)]" />
    </aside>
  )
}
