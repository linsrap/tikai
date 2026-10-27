import type { components } from '../api/schema'

export type RunEventInfo = components['schemas']['RunEventInfo']

/** Events per request; the API's maximum. */
export const pageSize = 1000

/**
 * A run's transcript as far as the browser has it: events in seq order without gaps or duplicates.
 *
 * The server numbers events from a counter on the run row inside the inserting transaction, so they become visible in
 * seq order: "everything after the last seq I have" is always complete. Appending drops what is already there, so a
 * fetch that overlaps another (a live message during a fetch, a reconnect during paging) never shows a line twice.
 */
export class LogBuffer {
  private readonly items: RunEventInfo[] = []

  get events(): readonly RunEventInfo[] {
    return this.items
  }

  get lastSeq(): number {
    return this.items.length > 0 ? this.items[this.items.length - 1].seq : 0
  }

  /** Adds the events after the last one held; returns how many were new. */
  append(page: readonly RunEventInfo[]): number {
    let added = 0
    for (const event of [...page].sort((a, b) => a.seq - b.seq)) {
      if (event.seq > this.lastSeq) {
        this.items.push(event)
        added++
      }
    }
    return added
  }
}

/**
 * Fetches everything after the buffer's last seq, page by page, until a page is short. Returns how many events were
 * new. A failed page leaves what was fetched so far; the next call goes on from there.
 */
export async function catchUp(
  buffer: LogBuffer,
  fetchPage: (afterSeq: number, limit: number) => Promise<readonly RunEventInfo[]>,
): Promise<number> {
  let added = 0
  for (;;) {
    const page = await fetchPage(buffer.lastSeq, pageSize)
    added += buffer.append(page)
    if (page.length < pageSize) return added
  }
}

/**
 * Runs a task at most once at a time; a request while it runs makes it run once more afterwards. So a burst of "new
 * events" messages leads to one fetch in flight and one after it, never to parallel fetches.
 */
export function coalesce(task: () => Promise<void>): () => Promise<void> {
  let running: Promise<void> | null = null
  let again = false
  const run = async (): Promise<void> => {
    if (running) {
      again = true
      return running
    }
    running = (async () => {
      try {
        do {
          again = false
          await task()
        } while (again)
      } finally {
        running = null
      }
    })()
    return running
  }
  return run
}
