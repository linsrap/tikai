import { useVirtualizer } from '@tanstack/react-virtual'
import { useEffect, useRef, useState } from 'react'
import { eventSummary } from '../runs/runs'
import { Button } from '../ui/Button'
import { cx } from '../ui/cx'
import { ErrorText } from '../ui/form'
import { Dot } from '../ui/Tag'
import type { RunEventInfo } from './logBuffer'
import { useRunLog } from './useRunLog'

const time = (iso: string) =>
  new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })

const kindLabel: Record<RunEventInfo['kind'], string> = {
  system: 'system',
  message: 'message',
  tool_call: 'tool',
  tool_result: 'result',
  progress: 'progress',
  result: 'end',
  log: 'log',
}

const kindColor: Record<RunEventInfo['kind'], string> = {
  system: 'text-fg-subtle',
  message: 'text-fg-2',
  tool_call: 'text-run',
  tool_result: 'text-ok',
  progress: 'text-wait',
  result: 'text-fg',
  log: 'text-fg-subtle',
}

/**
 * A run's live transcript. Only the rows in view are rendered (a run can have 10 000 events). It follows the end while
 * scrolled to the bottom and keeps its place otherwise. A row opens to its raw payload. The data attributes say which
 * seqs it holds, for tests.
 */
export function RunLog({ runId }: { runId: string }) {
  const { events, error } = useRunLog(runId)
  const scroller = useRef<HTMLDivElement>(null)
  const [follow, setFollow] = useState(true)
  // TanStack Virtual returns functions that change between renders; the React Compiler is not used here.
  // eslint-disable-next-line react-hooks/incompatible-library
  const virtualizer = useVirtualizer({
    count: events.length,
    getScrollElement: () => scroller.current,
    estimateSize: () => 22,
    overscan: 20,
  })

  useEffect(() => {
    if (follow && events.length > 0) {
      virtualizer.scrollToIndex(events.length - 1, { align: 'end' })
    }
  }, [events.length, follow, virtualizer])

  return (
    <div className="flex flex-col gap-2">
      {error && <ErrorText>{error}</ErrorText>}
      <div className="overflow-hidden rounded-md border border-line bg-bg">
        <div className="flex items-center gap-2.5 border-b border-line px-3 py-1.5 text-xs text-fg-muted">
          <Dot tone={follow ? 'ok' : 'neutral'} />
          {follow ? 'Following the end' : 'Scrolled up'}
          <span className="tabular-nums">
            · {events.length} event{events.length === 1 ? '' : 's'}
          </span>
          {!follow && events.length > 0 && (
            <Button
              variant="ghost"
              size="sm"
              className="ml-auto h-6"
              onClick={() => setFollow(true)}
            >
              Follow the end
            </Button>
          )}
        </div>
        <div
          ref={scroller}
          className="h-72 overflow-auto font-mono text-xs leading-5"
          role="log"
          aria-label="Agent log"
          data-count={events.length}
          data-first-seq={events[0]?.seq ?? 0}
          data-last-seq={events.at(-1)?.seq ?? 0}
          onScroll={(e) => {
            const el = e.currentTarget
            setFollow(el.scrollHeight - el.scrollTop - el.clientHeight < 40)
          }}
        >
          {events.length === 0 && <p className="m-0 px-3 py-2 text-fg-subtle">No events yet.</p>}
          <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
            {virtualizer.getVirtualItems().map((row) => (
              <div
                key={row.key}
                data-index={row.index}
                ref={virtualizer.measureElement}
                className="absolute top-0 left-0 w-full"
                style={{ transform: `translateY(${row.start}px)` }}
              >
                <LogLine event={events[row.index]} />
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  )
}

function LogLine({ event }: { event: RunEventInfo }) {
  return (
    <details className={cx('group', `event ${event.kind}`)}>
      <summary className="flex cursor-pointer list-none gap-3 px-3 hover:bg-hover [&::-webkit-details-marker]:hidden">
        <span className="w-9 shrink-0 text-right text-fg-faint tabular-nums">{event.seq}</span>
        <span className="w-[4.5rem] shrink-0 text-fg-subtle tabular-nums max-sm:hidden">
          {time(event.createdAt)}
        </span>
        <span className={cx('w-16 shrink-0', kindColor[event.kind])}>{kindLabel[event.kind]}</span>
        <span className="min-w-0 truncate text-fg-2 group-open:[overflow-wrap:anywhere] group-open:whitespace-pre-wrap">
          {eventSummary(event)}
        </span>
      </summary>
      <pre className="m-0 mx-3 mb-2 ml-[3.75rem] overflow-x-auto rounded-md border border-line bg-surface p-2 text-[11px] leading-4 [overflow-wrap:anywhere] whitespace-pre-wrap text-fg-2">
        {JSON.stringify(event.payload, null, 2)}
      </pre>
    </details>
  )
}
