import { useSyncExternalStore } from 'react'
import type { RunInfo } from '../runs/runs'

/** How long a run says that it continued after an answer. */
export const continuedFor = 2 * 60_000

type Answered = {
  /** When an answer to each run was sent from this page, by run id. */
  runs: ReadonlyMap<string, number>
  /** The run answered last, with a counter, so each answer is seen once. */
  last: { runId: string; n: number } | null
}

let state: Answered = { runs: new Map(), last: null }
const listeners = new Set<() => void>()

/** Notes that a person answered one of the run's questions or plans (in this browser tab, until a reload). */
export function noteAnswered(runId: string, at = Date.now()) {
  const runs = new Map(state.runs)
  runs.set(runId, at)
  state = { runs, last: { runId, n: (state.last?.n ?? 0) + 1 } }
  listeners.forEach((listener) => listener())
}

function subscribe(listener: () => void) {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

/** The runs answered from this tab, and the latest answer. */
export const useAnswered = () =>
  useSyncExternalStore(
    subscribe,
    () => state,
    () => state,
  )

/** For tests. */
export function resetAnswered() {
  state = { runs: new Map(), last: null }
}

/**
 * Whether the run took up an answer sent from this tab: "running again" once it left waiting, "resuming" while a new
 * container starts; null when there was no answer in the last two minutes, or the run still waits for more answers.
 */
export function continuedLine(
  run: Pick<RunInfo, 'state' | 'agentStopped' | 'waiting'>,
  answeredAt: number | undefined,
  now = Date.now(),
): string | null {
  if (answeredAt == null || now - answeredAt > continuedFor) return null
  switch (run.state) {
    case 'running':
      return 'Your answer was delivered · running again'
    case 'queued':
    case 'preparing':
      return 'Resuming in a new container…'
    case 'blocked':
      return run.agentStopped && run.waiting.length === 0 ? 'Resuming in a new container…' : null
    default:
      return null
  }
}
