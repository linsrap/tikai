import { ago, analysisLine, coverageSummary, waitingCount } from './inbox'

describe('inbox', () => {
  it('counts the items an agent waits for', () => {
    expect(waitingCount(undefined)).toBe(0)
    expect(waitingCount([{ blocking: true }, { blocking: false }, { blocking: true }])).toBe(2)
  })

  it('sums up a review by coverage', () => {
    expect(
      coverageSummary([{ status: 'covered' }, { status: 'missing' }, { status: 'covered' }]),
    ).toBe('2 covered, 1 missing')
    expect(coverageSummary([])).toBe('')
  })

  it('sums up the review analysis in one line', () => {
    const brief = { covered: 0, partial: 0, missing: 0, highRisk: 0, error: null }
    expect(analysisLine({ ...brief, status: 'pending' })).toBe('Analysis: running…')
    expect(analysisLine({ ...brief, status: 'failed', error: 'No key.' })).toBe(
      'Analysis unavailable: No key.',
    )
    expect(analysisLine({ ...brief, status: 'done', covered: 3, missing: 1, highRisk: 1 })).toBe(
      'Analysis: 3 covered, 1 missing · 1 high-risk file',
    )
    expect(analysisLine({ ...brief, status: 'done' })).toBe(
      'Analysis: no criteria assessed · 0 high-risk files',
    )
  })

  it('says how long ago an item came in', () => {
    const now = new Date('2026-10-27T12:00:00Z').getTime()
    expect(ago('2026-10-27T11:59:30Z', now)).toBe('just now')
    expect(ago('2026-10-27T11:57:00Z', now)).toBe('3 min ago')
    expect(ago('2026-10-27T09:00:00Z', now)).toBe('3 h ago')
  })
})
