import { useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import { unwrap } from '../api/result'
import { live } from './live'
import { catchUp, coalesce, LogBuffer } from './logBuffer'

/**
 * A run's transcript that grows live: it watches the run, fetches everything after its last seq when the hub says
 * there is more, and again after a reconnect. The run is watched before the first fetch, so nothing written in
 * between is missed (the next message fetches it; duplicates are dropped by seq).
 */
export function useRunLog(runId: string) {
  const buffer = useMemo(() => new LogBuffer(), [runId]) // eslint-disable-line react-hooks/exhaustive-deps -- a new buffer per run
  const [, setVersion] = useState(0)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let active = true
    const sync = coalesce(async () => {
      try {
        const added = await catchUp(buffer, (afterSeq, limit) =>
          unwrap(
            api.GET('/api/runs/{id}/events', {
              params: { path: { id: runId }, query: { afterSeq, limit } },
            }),
            'load the log',
          ),
        )
        if (!active) return
        setError(null)
        if (added > 0) setVersion((v) => v + 1)
      } catch (e) {
        if (active) setError(e instanceof Error ? e.message : String(e))
      }
    })
    const unwatch = live.watch('WatchRun', runId)
    const offEvents = live.onRunEvents(runId, () => void sync())
    const offRefresh = live.onRefresh(() => void sync())
    void sync()
    return () => {
      active = false
      unwatch()
      offEvents()
      offRefresh()
    }
  }, [buffer, runId])

  return { events: buffer.events, lastSeq: buffer.lastSeq, error }
}
