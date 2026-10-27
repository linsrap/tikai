import { useVirtualizer } from '@tanstack/react-virtual'
import { useEffect, useRef, useState } from 'react'
import { eventSummary } from '../runs/runs'
import type { RunEventInfo } from './logBuffer'
import { useRunLog } from './useRunLog'

const time = (iso: string) => new Date(iso).toLocaleTimeString()

/**
 * A run's live transcript. Only the rows in view are rendered (a run can have 10 000 events). It follows the end while
 * scrolled to the bottom and keeps its place otherwise. The data attributes say which seqs it holds, for tests.
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
    estimateSize: () => 26,
    overscan: 20,
  })

  useEffect(() => {
    if (follow && events.length > 0) {
      virtualizer.scrollToIndex(events.length - 1, { align: 'end' })
    }
  }, [events.length, follow, virtualizer])

  return (
    <div className="stack">
      {error && <p className="error">{error}</p>}
      <div
        ref={scroller}
        className="log mono"
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
        {events.length === 0 && <p className="muted">No events yet.</p>}
        <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
          {virtualizer.getVirtualItems().map((row) => (
            <div
              key={row.key}
              data-index={row.index}
              ref={virtualizer.measureElement}
              className="log-row"
              style={{ transform: `translateY(${row.start}px)` }}
            >
              <LogLine event={events[row.index]} />
            </div>
          ))}
        </div>
      </div>
      {!follow && events.length > 0 && (
        <button type="button" onClick={() => setFollow(true)}>
          Follow the end
        </button>
      )}
    </div>
  )
}

function LogLine({ event }: { event: RunEventInfo }) {
  return (
    <details className={`event ${event.kind}`}>
      <summary>
        <span className="muted">
          {event.seq} {time(event.createdAt)}
        </span>{' '}
        {eventSummary(event)}
      </summary>
      <pre>{JSON.stringify(event.payload, null, 2)}</pre>
    </details>
  )
}
