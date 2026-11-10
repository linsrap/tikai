import { describe, expect, it } from 'vitest'
import { canStart, logStart, previewLine, type ChangeSetPreview, type Preview } from './preview'

const preview = (over: Partial<Preview> = {}): Preview => ({
  id: 'p1',
  changeSetId: 'c1',
  roundNo: 2,
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

describe('previewLine', () => {
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
