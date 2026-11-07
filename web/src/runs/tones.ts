import type { Tone } from '../ui/Tag'
import type { RunState } from './runs'

/** Colour of a run state: blue while it works, amber while it waits for a person, then how it ended. */
export const runStateTone: Record<RunState, Tone> = {
  queued: 'neutral',
  preparing: 'run',
  running: 'run',
  blocked: 'wait',
  in_review: 'wait',
  changes_requested: 'run',
  merged: 'ok',
  pushed: 'ok',
  failed: 'bad',
  cancelled: 'neutral',
}
