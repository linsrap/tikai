import {
  anchorFor,
  fileThreads,
  openThreadCounts,
  paneOf,
  threadPlace,
  unsentForAgent,
  viewLabel,
  type ReviewComment,
  type ReviewThread,
} from './review'

const comment = (extra: Partial<ReviewComment> = {}): ReviewComment => ({
  id: 'c',
  author: 'person',
  authorName: 'Ada',
  body: 'Fix this.',
  action: 'note',
  createdAt: '2026-11-03T10:00:00Z',
  editedAt: null,
  sentAt: null,
  canEdit: true,
  ...extra,
})

const thread = (id: string, extra: Partial<ReviewThread> = {}): ReviewThread => ({
  id,
  roundNo: 2,
  path: 'src/a.ts',
  side: 'new',
  lineStart: 3,
  lineEnd: 3,
  status: 'open',
  createdAt: '2026-11-03T10:00:00Z',
  resolvedBy: null,
  resolvedAt: null,
  comments: [comment()],
  ...extra,
})

const heads = { roundNo: 2, interdiff: 'heads' as const }
const full = { roundNo: 2, interdiff: 'none' as const }

describe('review threads', () => {
  it('anchors the left pane of an interdiff to the round before and the full diff to the base', () => {
    expect(anchorFor(heads, 'original')).toEqual({ roundNo: 1, side: 'new' })
    expect(anchorFor(heads, 'modified')).toEqual({ roundNo: 2, side: 'new' })
    expect(anchorFor(full, 'original')).toEqual({ roundNo: 2, side: 'old' })
    expect(anchorFor({ roundNo: 2, interdiff: 'per_file' }, 'modified')).toEqual({
      roundNo: 2,
      side: 'new',
    })
  })

  it('finds the pane a thread is in, or none', () => {
    expect(paneOf({ roundNo: 1, side: 'new' }, heads)).toBe('original')
    expect(paneOf({ roundNo: 2, side: 'new' }, heads)).toBe('modified')
    expect(paneOf({ roundNo: 2, side: 'old' }, heads)).toBeNull()
    expect(paneOf({ roundNo: 1, side: 'new' }, full)).toBeNull()
    expect(paneOf({ roundNo: 2, side: 'old' }, full)).toBe('original')
  })

  it('shows a file’s threads at their lines when the diff has that version, and lists the others', () => {
    const list = [
      thread('earlier', { roundNo: 1 }),
      thread('now', { roundNo: 2 }),
      thread('base', { roundNo: 2, side: 'old' }),
      thread('other file', { path: 'src/b.ts' }),
      thread('general', { path: null, side: null, lineStart: null, lineEnd: null }),
      thread('oldest', { roundNo: 0 }),
    ]
    const file = { path: 'src/a.ts', oldPath: null }

    const interdiff = fileThreads(list, file, heads)
    expect(interdiff.inline.map((i) => [i.thread.id, i.pane])).toEqual([
      ['earlier', 'original'],
      ['now', 'modified'],
    ])
    expect(interdiff.listed.map((t) => t.id)).toEqual(['base', 'oldest'])

    const whole = fileThreads(list, file, full)
    expect(whole.inline.map((i) => [i.thread.id, i.pane])).toEqual([
      ['now', 'modified'],
      ['base', 'original'],
    ])
    expect(whole.listed.map((t) => t.id)).toEqual(['earlier', 'oldest'])

    // The inline diff has no left pane.
    expect(fileThreads(list, file, heads, false).listed.map((t) => t.id)).toEqual([
      'earlier',
      'base',
      'oldest',
    ])
  })

  it('uses the old path of a renamed file for the left pane', () => {
    const renamed = { path: 'src/new.ts', oldPath: 'src/old.ts' }
    const list = [
      thread('left', { roundNo: 1, path: 'src/old.ts' }),
      thread('right', { path: 'src/new.ts' }),
    ]

    expect(fileThreads(list, renamed, heads).inline.map((i) => i.thread.id)).toEqual([
      'left',
      'right',
    ])
  })

  it('counts open threads per file and unsent comments for the agent', () => {
    const list = [
      thread('a', { comments: [comment({ action: 'send_to_agent' }), comment()] }),
      thread('b', {
        comments: [comment({ action: 'send_to_agent', sentAt: '2026-11-03T11:00:00Z' })],
      }),
      thread('c', { status: 'resolved', comments: [comment({ action: 'send_to_agent' })] }),
      thread('d', { path: null, side: null, comments: [comment({ action: 'send_to_agent' })] }),
    ]

    expect(openThreadCounts(list)).toEqual(new Map([['src/a.ts', 2]]))
    expect(unsentForAgent(list)).toBe(2)
  })

  it('names what is shown and where a thread is', () => {
    expect(viewLabel({ view: 'interdiff', previousRound: 1 })).toBe('Changes since round 1')
    expect(viewLabel({ view: 'interdiff', previousRound: null })).toBe('Full change')
    expect(viewLabel({ view: 'full', previousRound: 1 })).toBe('Full change')
    expect(threadPlace(thread('t', { lineEnd: 5 }))).toBe(
      'src/a.ts, lines 3–5 (round 2, new version)',
    )
    expect(threadPlace(thread('t', { side: 'old' }))).toBe(
      'src/a.ts, line 3 (round 2, old version)',
    )
    expect(threadPlace(thread('t', { path: null }))).toBe('General comment (round 2)')
  })
})
