import { describe, expect, it } from 'vitest'
import {
  appsLine,
  canStart,
  canStartBranch,
  filterLog,
  logFilterOptions,
  logStart,
  previewLine,
  type ChangeSetPreview,
  type Preview,
  type RepositoryPreview,
  type PreviewLogLine,
} from './preview'

const preview = (over: Partial<Preview> = {}): Preview => ({
  id: 'p1',
  repositoryId: 'r1',
  changeSetId: 'c1',
  roundNo: 2,
  branch: null,
  headSha: 'abc',
  state: 'running',
  error: null,
  stopReason: null,
  startedAt: '2026-11-10T10:00:00Z',
  runningAt: '2026-11-10T10:02:00Z',
  stoppedAt: null,
  idleMinutes: 30,
  lastLogSeq: 12,
  url: 'http://p1.preview.localhost:8080',
  apps: [{ name: 'app', path: '/', logSource: 'app' }],
  ...over,
})

const info = (over: Partial<ChangeSetPreview> = {}): ChangeSetPreview => ({
  available: true,
  unavailableReason: null,
  definition: { roundNo: 2, found: true, errors: [] },
  preview: null,
  ...over,
})

describe('canStart', () => {
  it('starts the latest round of an open change set with a usable definition', () => {
    expect(canStart(info(), true)).toBe(true)
  })

  it('does not start without a usable definition, on a closed change set, or where previews are off', () => {
    expect(canStart(info({ definition: { roundNo: 2, found: false, errors: [] } }), true)).toBe(
      false,
    )
    expect(
      canStart(info({ definition: { roundNo: 2, found: true, errors: ['app: required'] } }), true),
    ).toBe(false)
    expect(canStart(info(), false)).toBe(false)
    expect(canStart(info({ available: false, unavailableReason: 'off' }), true)).toBe(false)
  })

  it('does not start the round again while its preview is active, but does for a newer round or after it ended', () => {
    expect(canStart(info({ preview: preview({ state: 'starting' }) }), true)).toBe(false)
    expect(canStart(info({ preview: preview({ roundNo: 1 }) }), true)).toBe(true)
    expect(canStart(info({ preview: preview({ state: 'stopped' }) }), true)).toBe(true)
  })
})

const branchPreview = (over: Partial<Preview> = {}): Preview =>
  preview({
    changeSetId: null,
    roundNo: null,
    branch: 'main',
    headSha: '1a2b3c4d5e6f7a8b',
    ...over,
  })

const repository = (over: Partial<RepositoryPreview> = {}): RepositoryPreview => ({
  available: true,
  unavailableReason: null,
  branch: 'main',
  headSha: '1a2b3c4d5e6f7a8b',
  found: true,
  errors: [],
  preview: null,
  ...over,
})

describe('canStartBranch', () => {
  it('starts the head of the default branch when it has a usable definition', () => {
    expect(canStartBranch(repository())).toBe(true)
    expect(canStartBranch(repository({ found: false }))).toBe(false)
    expect(canStartBranch(repository({ errors: ['app: required'] }))).toBe(false)
    expect(canStartBranch(repository({ available: false, unavailableReason: 'archived' }))).toBe(
      false,
    )
  })

  it('does not start the same head again while it runs, but does a newer head', () => {
    expect(canStartBranch(repository({ preview: branchPreview() }))).toBe(false)
    expect(canStartBranch(repository({ preview: branchPreview({ headSha: 'old' }) }))).toBe(true)
    expect(canStartBranch(repository({ preview: branchPreview({ state: 'stopped' }) }))).toBe(true)
  })
})

describe('previewLine', () => {
  it('names the branch and its commit for a preview of the default branch', () => {
    expect(previewLine(branchPreview({ state: 'preparing' }))).toBe(
      'Cloning main at 1a2b3c4 and running its setup…',
    )
    expect(previewLine(branchPreview())).toBe(
      'main at 1a2b3c4 runs. It stops after 30 minutes without use, or when main moves.',
    )
    expect(
      previewLine(
        branchPreview({
          state: 'stopped',
          stopReason: 'main moved to 9f8e7d6c5b4a. Start a new preview to see it.',
        }),
      ),
    ).toBe(
      'The preview of main at 1a2b3c4 stopped. main moved to 9f8e7d6c5b4a. Start a new preview to see it.',
    )
  })

  it('says what happens and why it ended', () => {
    expect(previewLine(preview({ state: 'preparing' }))).toBe(
      'Cloning round 2 and running its setup…',
    )
    expect(previewLine(preview())).toBe('Round 2 runs. It stops after 30 minutes without use.')
    expect(
      previewLine(preview({ state: 'stopped', stopReason: 'Nobody used it for 30 minutes.' })),
    ).toBe('The preview of round 2 stopped. Nobody used it for 30 minutes.')
    expect(
      previewLine(
        preview({
          state: 'failed',
          error: 'Setup step 1 failed with exit code 1; see the preview log.',
        }),
      ),
    ).toBe(
      'The preview of round 2 failed: Setup step 1 failed with exit code 1; see the preview log.',
    )
  })
})

describe('logStart', () => {
  it('shows the latest lines', () => {
    expect(logStart(12)).toBe(0)
    expect(logStart(1200)).toBe(700)
  })
})

describe('several apps', () => {
  const apps = [
    { name: 'web', path: '/', logSource: 'app web' },
    { name: 'api', path: '/api', logSource: 'app api' },
  ]
  const line = (seq: number, source: string): PreviewLogLine => ({
    seq,
    source,
    text: 't',
    at: '2026-11-10T10:00:00Z',
  })

  it('says which app serves which path, only when there are several', () => {
    expect(appsLine(apps)).toBe('Apps: web on /, api on /api')
    expect(appsLine([{ name: 'app', path: '/', logSource: 'app' }])).toBeNull()
  })

  it('offers a log filter per app only when there are several', () => {
    expect(logFilterOptions(apps).map((o) => o.label)).toEqual(['All', 'web', 'api'])
    expect(logFilterOptions(apps.slice(0, 1))).toEqual([])
  })

  it("shows one app's lines with the hub's notes", () => {
    const lines = [line(1, 'hub'), line(2, 'app web'), line(3, 'app api'), line(4, 'setup 1')]
    expect(filterLog(lines, 'app api').map((l) => l.seq)).toEqual([1, 3])
    expect(filterLog(lines, 'all')).toHaveLength(4)
  })
})
