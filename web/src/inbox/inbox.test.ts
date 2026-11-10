import {
  ago,
  analysisLine,
  coverageSummary,
  filesByFolder,
  orderedOptions,
  planDetails,
  planHeadline,
  roundLine,
  selfReportLine,
  waitingCount,
} from './inbox'

describe('inbox', () => {
  it('heads a plan with its summary, or its first step when it has none', () => {
    expect(planHeadline({ summary: 'Add a login page.', steps: ['Read', 'Write'] })).toBe(
      'Add a login page.',
    )
    expect(planHeadline({ summary: null, steps: ['Read', 'Write'] })).toBe('Read')
    expect(planHeadline({ steps: [] })).toBe('')
  })

  it('says what a plan holds', () => {
    expect(planDetails({ steps: ['a'], risks: [], filesExpected: [] })).toBe('1 step')
    expect(planDetails({ steps: ['a', 'b'], risks: ['r'], filesExpected: ['x', 'y'] })).toBe(
      '2 steps, 1 risk, 2 files',
    )
  })

  it('groups expected files by folder in the agent order', () => {
    expect(
      filesByFolder([
        'src/Api/Reviews/A.cs',
        'README.md',
        'src/Api/Reviews/B.cs',
        'web\\src\\x.ts',
        'docs/',
      ]),
    ).toEqual([
      { folder: 'src/Api/Reviews', names: ['A.cs', 'B.cs'] },
      { folder: '', names: ['README.md', 'docs'] },
      { folder: 'web/src', names: ['x.ts'] },
    ])
  })

  it('sums up a collapsed self-report in one line', () => {
    expect(selfReportLine([], [])).toBe('')
    expect(selfReportLine([{ status: 'covered' }, { status: 'partial' }], ['a'])).toBe(
      'Criteria: 1 covered, 1 partial · 1 open concern',
    )
    expect(selfReportLine([], ['a', 'b'])).toBe('2 open concerns')
  })

  it('puts the recommended option first', () => {
    expect(orderedOptions({ options: ['a', 'b', 'c'], recommended: 'b' })).toEqual(['b', 'a', 'c'])
    expect(orderedOptions({ options: ['a', 'b'], recommended: null })).toEqual(['a', 'b'])
    expect(orderedOptions({ options: ['a', 'b'], recommended: 'z' })).toEqual(['a', 'b'])
  })

  it('names a later round and how the agent answered the comments', () => {
    expect(roundLine(null)).toBeNull()
    expect(roundLine({ roundNo: 1, answered: 0, unanswered: 0 })).toBeNull()
    expect(roundLine({ roundNo: 2, answered: 0, unanswered: 0 })).toBe('Round 2')
    expect(roundLine({ roundNo: 2, answered: 2, unanswered: 1 })).toBe(
      'Round 2 · the agent answered 2 of 3 threads',
    )
    expect(roundLine({ roundNo: 3, answered: 1, unanswered: 0 })).toBe(
      'Round 3 · the agent answered 1 of 1 thread',
    )
  })

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
    const brief = {
      covered: 0,
      partial: 0,
      missing: 0,
      highRisk: 0,
      error: null,
      criteria: 0,
      closeLook: 0,
    }
    expect(analysisLine({ ...brief, status: 'pending' })).toBe('Analysis: running…')
    expect(analysisLine({ ...brief, status: 'failed', error: 'No key.' })).toBe(
      'Analysis unavailable: No key.',
    )
    expect(
      analysisLine({ ...brief, status: 'done', criteria: 5, covered: 3, missing: 1, closeLook: 1 }),
    ).toBe('Meets 3/5 criteria · 1 missing · 1 not assessed · 1 file needs a close look')
    expect(analysisLine({ ...brief, status: 'done' })).toBe('No criteria')
  })

  it('says how long ago an item came in', () => {
    const now = new Date('2026-10-27T12:00:00Z').getTime()
    expect(ago('2026-10-27T11:59:30Z', now)).toBe('just now')
    expect(ago('2026-10-27T11:57:00Z', now)).toBe('3 min ago')
    expect(ago('2026-10-27T09:00:00Z', now)).toBe('3 h ago')
  })
})
