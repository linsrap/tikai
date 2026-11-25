import { useVirtualizer } from '@tanstack/react-virtual'
import { useEffect, useMemo, useRef, useState, useSyncExternalStore, type ReactNode } from 'react'
import { eventSummary } from '../runs/runs'
import { Button } from '../ui/Button'
import { cx } from '../ui/cx'
import { ErrorText } from '../ui/form'
import { Segmented } from '../ui/Segmented'
import { Dot } from '../ui/Tag'
import { ChatBuilder } from './chat'
import type { RunEventInfo } from './logBuffer'
import { logTime } from './logTime'
import { ChatRow } from './RunChat'
import { useRunLog } from './useRunLog'

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

export type LogView = 'chat' | 'raw'

// The chosen view, per browser; storage can be blocked, then it holds for the page.
const viewKey = 'tikai.logView'
const viewListeners = new Set<() => void>()
let viewChoice: LogView = (() => {
  try {
    return localStorage.getItem(viewKey) === 'raw' ? 'raw' : 'chat'
  } catch {
    return 'chat'
  }
})()

function setLogView(next: LogView) {
  viewChoice = next
  try {
    localStorage.setItem(viewKey, next)
  } catch {
    // ignored: see above
  }
  viewListeners.forEach((l) => l())
}

function useLogView() {
  return useSyncExternalStore(
    (listener) => {
      viewListeners.add(listener)
      return () => viewListeners.delete(listener)
    },
    () => viewChoice,
  )
}

/**
 * A run's live transcript, as a chat (text, tool calls with their results, sessions) or raw (one line per event,
 * opening to its payload). Only the rows in view are rendered (a run can have 10 000 events). It follows the end
 * while scrolled to the bottom and keeps its place otherwise. The data attributes say which seqs it holds, for tests.
 */
export function RunLog({
  runId,
  height = 'h-72',
}: {
  runId: string
  /** The scroll area's height class; the inbox's side panel fills the window. */
  height?: string
}) {
  const { events, error } = useRunLog(runId)
  const view = useLogView()
  const [follow, setFollow] = useState(true)
  const chat = useMemo(() => new ChatBuilder(), [runId]) // eslint-disable-line react-hooks/exhaustive-deps -- one per run
  // The builder only reads events it has not seen, so this is cheap on every render.
  chat.update(events)
  const opened = useMemo(() => new Map<string, boolean>(), [runId]) // eslint-disable-line react-hooks/exhaustive-deps -- one per run

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
          <div className="ml-auto flex items-center gap-2">
            {!follow && events.length > 0 && (
              <Button variant="ghost" size="sm" className="h-6" onClick={() => setFollow(true)}>
                Follow the end
              </Button>
            )}
            <Segmented
              label="Log view"
              size="sm"
              value={view}
              onChange={setLogView}
              options={[
                { value: 'chat', label: 'Chat' },
                { value: 'raw', label: 'Raw' },
              ]}
            />
          </div>
        </div>
        {view === 'chat' ? (
          <Scroller
            key="chat"
            className={cx(height, 'text-sm')}
            events={events}
            count={chat.items.length}
            itemKey={(i) => chat.items[i].key}
            estimate={44}
            follow={follow}
            setFollow={setFollow}
            row={(i) => <ChatRow item={chat.items[i]} cwd={chat.cwd} opened={opened} />}
          />
        ) : (
          <Scroller
            key="raw"
            className={cx(height, 'font-mono text-xs leading-5')}
            events={events}
            count={events.length}
            itemKey={(i) => String(events[i].seq)}
            estimate={22}
            follow={follow}
            setFollow={setFollow}
            row={(i) => <LogLine event={events[i]} />}
          />
        )}
      </div>
    </div>
  )
}

/** The virtualized scroll area both views share. */
function Scroller({
  className,
  events,
  count,
  itemKey,
  estimate,
  follow,
  setFollow,
  row,
}: {
  className: string
  events: readonly RunEventInfo[]
  count: number
  itemKey: (index: number) => string
  estimate: number
  follow: boolean
  setFollow: (follow: boolean) => void
  row: (index: number) => ReactNode
}) {
  const scroller = useRef<HTMLDivElement>(null)
  // TanStack Virtual returns functions that change between renders; the React Compiler is not used here.
  // eslint-disable-next-line react-hooks/incompatible-library
  const virtualizer = useVirtualizer({
    count,
    getScrollElement: () => scroller.current,
    estimateSize: () => estimate,
    getItemKey: itemKey,
    overscan: 20,
  })

  // Every new event may grow the last row (a tool result) without adding one, so this follows events, not rows.
  useEffect(() => {
    if (follow && count > 0) {
      virtualizer.scrollToIndex(count - 1, { align: 'end' })
    }
  }, [events.length, count, follow, virtualizer])

  return (
    <div
      ref={scroller}
      className={cx(className, 'overflow-auto')}
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
      {count === 0 && <p className="m-0 px-3 py-2 text-xs text-fg-subtle">No events yet.</p>}
      <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
        {virtualizer.getVirtualItems().map((item) => (
          <div
            key={item.key}
            data-index={item.index}
            ref={virtualizer.measureElement}
            className="absolute top-0 left-0 w-full"
            style={{ transform: `translateY(${item.start}px)` }}
          >
            {row(item.index)}
          </div>
        ))}
      </div>
    </div>
  )
}

function LogLine({ event }: { event: RunEventInfo }) {
  return (
    <details className={cx('group', `event ${event.kind}`)}>
      {/* Fixed columns are sized in characters of the mono font and never wrap; only the summary is cut. */}
      <summary className="flex cursor-pointer list-none gap-3 px-3 whitespace-nowrap hover:bg-hover [&::-webkit-details-marker]:hidden">
        <span className="min-w-[5ch] shrink-0 text-right text-fg-faint tabular-nums">
          {event.seq}
        </span>
        <time
          dateTime={event.createdAt}
          title={new Date(event.createdAt).toLocaleString()}
          className="w-[8ch] shrink-0 text-fg-subtle tabular-nums max-sm:hidden"
        >
          {logTime(event.createdAt)}
        </time>
        <span className={cx('w-[8ch] shrink-0', kindColor[event.kind])}>
          {kindLabel[event.kind]}
        </span>
        <span className="min-w-0 truncate text-fg-2 group-open:[overflow-wrap:anywhere] group-open:whitespace-pre-wrap">
          {eventSummary(event)}
        </span>
      </summary>
      <pre className="m-0 mx-3 mb-2 ml-[calc(5ch+1.5rem)] overflow-x-auto rounded-md border border-line bg-surface p-2 text-[11px] leading-4 [overflow-wrap:anywhere] whitespace-pre-wrap text-fg-2">
        {JSON.stringify(event.payload, null, 2)}
      </pre>
    </details>
  )
}
