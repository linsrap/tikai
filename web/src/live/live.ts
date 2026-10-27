import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr'
import { useQueryClient, type QueryClient } from '@tanstack/react-query'
import { useEffect, useSyncExternalStore } from 'react'

/** connecting: first start; live: connected; reconnecting: lost, trying again (pages still work, without updates). */
export type LiveStatus = 'off' | 'connecting' | 'live' | 'reconnecting'

type RunChange = { runId: string; projectSlug: string; ticketNumber: number }
type AttentionChange = { itemId: string; projectSlug: string; ticketNumber: number }
type ChangeSetChange = { changeSetId: string; projectSlug: string; ticketNumber: number }
type RunEventsChange = { runId: string }

/** Wait before the next attempt: at once, 2 s, 5 s, 10 s, then every 30 s, for as long as it takes. */
export const retryDelay = (previousRetryCount: number): number =>
  [0, 2000, 5000, 10000][previousRetryCount] ?? 30000

/** The queries a change makes stale. The client re-reads them; the hub sends ids only. */
export function staleKeys(
  message: 'RunChanged' | 'AttentionChanged' | 'ChangeSetChanged',
  change: { projectSlug: string; ticketNumber: number; changeSetId?: string },
): unknown[][] {
  const ticket = ['projects', change.projectSlug, 'tickets', change.ticketNumber]
  switch (message) {
    case 'RunChanged':
      // The ticket's status follows its run; inbox cards show the run's state and step; the review shows the run's state.
      return [
        ['runs'],
        [...ticket, 'runs'],
        ['projects', change.projectSlug, 'tickets'],
        ['attention-items'],
        ['change-sets'],
      ]
    case 'AttentionChanged':
      // The board and the ticket page show what a run waits for.
      return [['attention-items'], [...ticket, 'runs'], ['runs']]
    case 'ChangeSetChanged':
      // A new round or a verdict: the review itself (not its files: a round never changes), and the links to it.
      return [
        ['change-sets', change.changeSetId],
        [...ticket, 'runs'],
        ['runs'],
        ['attention-items'],
      ]
  }
}

/** Everything a page can show from live data, re-read after a reconnect or a resync. */
export const liveKeys: unknown[][] = [['attention-items'], ['runs'], ['projects'], ['change-sets']]

type Watch = { method: 'WatchAll' | 'WatchProject' | 'WatchRun'; arg?: string; count: number }

/**
 * The browser's one hub connection. Pages say what they watch (all projects, a project, a run); the groups are per
 * connection, so after a reconnect every watch is sent again, and everything shown is re-read.
 */
class Live {
  private connection: HubConnection | null = null
  private queryClient: QueryClient | null = null
  private readonly watches = new Map<string, Watch>()
  private readonly runListeners = new Map<string, Set<() => void>>()
  private readonly refreshListeners = new Set<() => void>()
  private readonly statusListeners = new Set<() => void>()
  private currentStatus: LiveStatus = 'off'
  private startTimer: ReturnType<typeof setTimeout> | null = null
  private attempts = 0

  get status(): LiveStatus {
    return this.currentStatus
  }

  subscribeStatus = (listener: () => void) => {
    this.statusListeners.add(listener)
    return () => this.statusListeners.delete(listener)
  }

  start(queryClient: QueryClient) {
    this.queryClient = queryClient
    if (this.connection) return
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/live')
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (c) => retryDelay(c.previousRetryCount),
      })
      .configureLogging(LogLevel.Warning)
      .build()
    connection.on('RunChanged', (change: RunChange) =>
      this.invalidate(staleKeys('RunChanged', change)),
    )
    connection.on('AttentionChanged', (change: AttentionChange) =>
      this.invalidate(staleKeys('AttentionChanged', change)),
    )
    connection.on('ChangeSetChanged', (change: ChangeSetChange) =>
      this.invalidate(staleKeys('ChangeSetChanged', change)),
    )
    connection.on('RunEvents', (change: RunEventsChange) =>
      this.runListeners.get(change.runId)?.forEach((listener) => listener()),
    )
    connection.on('Resync', () => this.refresh())
    connection.onreconnecting(() => this.setStatus('reconnecting'))
    connection.onreconnected(() => void this.connected())
    // The automatic reconnect gave up (it does not, with this policy) or the server closed: start over.
    connection.onclose(() => {
      if (this.connection === connection) this.scheduleStart(connection)
    })
    this.connection = connection
    this.setStatus('connecting')
    this.scheduleStart(connection, 0)
  }

  /** Closes the connection, e.g. at logout: the next session starts a new one with its own cookie. */
  stop() {
    const connection = this.connection
    this.connection = null
    if (this.startTimer) clearTimeout(this.startTimer)
    this.attempts = 0
    this.setStatus('off')
    void connection?.stop()
  }

  /** Watches a group while the returned function has not been called. Watches are counted (several components). */
  watch(method: Watch['method'], arg?: string): () => void {
    const key = `${method}:${arg ?? ''}`
    const existing = this.watches.get(key)
    if (existing) {
      existing.count++
    } else {
      const watch: Watch = { method, arg, count: 1 }
      this.watches.set(key, watch)
      void this.send(watch)
    }
    return () => {
      const watch = this.watches.get(key)
      if (!watch || --watch.count > 0) return
      this.watches.delete(key)
      // Leaving a project or "all" is not worth a call: the client ignores what it does not show.
      if (method === 'WatchRun' && arg && this.connection?.state === HubConnectionState.Connected) {
        void this.connection.invoke('UnwatchRun', arg).catch(() => {})
      }
    }
  }

  /** Calls the listener when the run has new events. */
  onRunEvents(runId: string, listener: () => void): () => void {
    const listeners = this.runListeners.get(runId) ?? new Set()
    listeners.add(listener)
    this.runListeners.set(runId, listeners)
    return () => {
      listeners.delete(listener)
      if (listeners.size === 0) this.runListeners.delete(runId)
    }
  }

  /** Calls the listener after a reconnect or a resync, when changes may have been missed. */
  onRefresh(listener: () => void): () => void {
    this.refreshListeners.add(listener)
    return () => this.refreshListeners.delete(listener)
  }

  private scheduleStart(connection: HubConnection, delay = retryDelay(this.attempts)) {
    if (this.startTimer) clearTimeout(this.startTimer)
    this.setStatus(this.attempts === 0 ? 'connecting' : 'reconnecting')
    this.startTimer = setTimeout(async () => {
      if (this.connection !== connection) return
      try {
        await connection.start()
        await this.connected()
      } catch {
        this.attempts++
        this.scheduleStart(connection)
      }
    }, delay)
  }

  private async connected() {
    const wasConnectedBefore = this.currentStatus !== 'connecting'
    this.attempts = 0
    await Promise.all([...this.watches.values()].map((watch) => this.send(watch)))
    this.setStatus('live')
    // Whatever happened while no messages came: read it now. On the very first start the pages are loading anyway.
    if (wasConnectedBefore) this.refresh()
  }

  private async send(watch: Watch) {
    const connection = this.connection
    if (connection?.state !== HubConnectionState.Connected) return // sent on (re)connect
    try {
      await (watch.arg === undefined
        ? connection.invoke(watch.method)
        : connection.invoke(watch.method, watch.arg))
    } catch {
      // The connection dropped meanwhile; the reconnect sends it again.
    }
  }

  private refresh() {
    this.invalidate(liveKeys)
    this.refreshListeners.forEach((listener) => listener())
  }

  private invalidate(keys: unknown[][]) {
    for (const queryKey of keys) {
      void this.queryClient?.invalidateQueries({ queryKey })
    }
  }

  private setStatus(status: LiveStatus) {
    if (this.currentStatus === status) return
    this.currentStatus = status
    this.statusListeners.forEach((listener) => listener())
  }
}

export const live = new Live()

/** Starts live updates while a logged-in page is shown. */
export function useLiveConnection() {
  const queryClient = useQueryClient()
  useEffect(() => live.start(queryClient), [queryClient])
}

export const useLiveStatus = () => useSyncExternalStore(live.subscribeStatus, () => live.status)

/** Watches every project (inbox, board) or one project (ticket page) while the component is mounted. */
export function useWatch(method: 'WatchAll' | 'WatchProject', arg?: string) {
  useEffect(() => live.watch(method, arg), [method, arg])
}
