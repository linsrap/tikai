import { catchUp, coalesce, LogBuffer, pageSize, type RunEventInfo } from './logBuffer'

const event = (seq: number): RunEventInfo => ({
  seq,
  kind: 'log',
  payload: { source: 'hub', text: `line ${seq}` },
  createdAt: '2026-10-27T10:00:00Z',
})

const range = (from: number, to: number) =>
  Array.from({ length: to - from + 1 }, (_, i) => event(from + i))

/** A server holding events 1…n that answers like GET /api/runs/{id}/events. */
function server(count: () => number) {
  const calls: number[] = []
  const fetchPage = async (afterSeq: number, limit: number) => {
    calls.push(afterSeq)
    return range(afterSeq + 1, Math.min(count(), afterSeq + limit))
  }
  return { calls, fetchPage }
}

describe('LogBuffer', () => {
  it('keeps events in seq order and drops what it already has', () => {
    const buffer = new LogBuffer()
    expect(buffer.append(range(1, 3))).toBe(3)
    expect(buffer.append([event(5), event(4), event(2)])).toBe(2)
    expect(buffer.append(range(1, 5))).toBe(0)
    expect(buffer.events.map((e) => e.seq)).toEqual([1, 2, 3, 4, 5])
    expect(buffer.lastSeq).toBe(5)
  })

  it('starts empty at seq 0', () => {
    expect(new LogBuffer().lastSeq).toBe(0)
  })
})

describe('catchUp', () => {
  it('pages until a page is short', async () => {
    const buffer = new LogBuffer()
    const { calls, fetchPage } = server(() => 2 * pageSize + 7)

    expect(await catchUp(buffer, fetchPage)).toBe(2 * pageSize + 7)

    expect(calls).toEqual([0, pageSize, 2 * pageSize])
    expect(buffer.events.map((e) => e.seq)).toEqual(range(1, 2 * pageSize + 7).map((e) => e.seq))
  })

  it('goes on from the last seq after a reconnect, without gaps or duplicates', async () => {
    let count = 10
    const buffer = new LogBuffer()
    const { calls, fetchPage } = server(() => count)
    await catchUp(buffer, fetchPage)

    // Offline for a while; the agent went on.
    count = 25
    expect(await catchUp(buffer, fetchPage)).toBe(15)

    expect(calls).toEqual([0, 10])
    expect(buffer.events.map((e) => e.seq)).toEqual(range(1, 25).map((e) => e.seq))
  })

  it('keeps the pages it got when a later page fails, and the next call continues', async () => {
    const buffer = new LogBuffer()
    let fail = true
    const fetchPage = async (afterSeq: number, limit: number) => {
      if (afterSeq >= pageSize && fail) throw new Error('offline')
      return range(afterSeq + 1, Math.min(pageSize + 3, afterSeq + limit))
    }

    await expect(catchUp(buffer, fetchPage)).rejects.toThrow('offline')
    expect(buffer.lastSeq).toBe(pageSize)
    fail = false
    expect(await catchUp(buffer, fetchPage)).toBe(3)
    expect(buffer.events).toHaveLength(pageSize + 3)
  })
})

describe('coalesce', () => {
  it('runs once at a time and once more for requests made meanwhile', async () => {
    let runs = 0
    let release: () => void = () => {}
    const task = coalesce(async () => {
      runs++
      await new Promise<void>((resolve) => (release = resolve))
    })

    const first = task()
    void task()
    void task()
    expect(runs).toBe(1)
    release()
    await Promise.resolve()
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(runs).toBe(2)
    release()
    await first
    expect(runs).toBe(2)
  })
})
