import {
  coverageRows,
  fileOrder,
  loadOrderMode,
  nextUnviewed,
  refText,
  riskOf,
  saveOrderMode,
  storyFiles,
  type ReviewAnalysis,
  type ReviewFile,
} from './review'

const file = (path: string, extra: Partial<ReviewFile> = {}): ReviewFile => ({
  path,
  oldPath: null,
  kind: 'modified',
  additions: 1,
  deletions: 0,
  binary: false,
  lock: false,
  generated: false,
  large: false,
  viewed: false,
  language: 'plaintext',
  ...extra,
})

const files = [
  file('web/src/Login.tsx'),
  file('src/Api/Login.cs'),
  file('src/Data/User.cs'),
  file('README.md'),
  file('package-lock.json', { lock: true }),
]

const story: Pick<ReviewAnalysis, 'groups' | 'risks' | 'coverage'> = {
  groups: [
    { title: 'User table', layer: 'data', note: 'A column.', files: ['src/Data/User.cs'] },
    {
      title: 'Login',
      layer: 'api',
      note: 'Endpoint and page.',
      files: ['src/Api/Login.cs', 'web/src/Login.tsx', 'gone.cs'],
    },
    { title: 'Nothing left', layer: 'ui', note: '', files: ['gone.cs'] },
  ],
  risks: [
    { path: 'src/Api/Login.cs', risk: 'high', reason: 'Passwords.' },
    { path: 'README.md', risk: 'low', reason: 'Docs.' },
  ],
  coverage: [
    { criterionId: 'c1', status: 'covered', note: 'Login.cs', refs: [] },
    { criterionId: 'c2', status: 'missing', note: 'No test.', refs: [] },
  ],
}

describe('review analysis', () => {
  it('lists files in story order, unplaced files under other changes, skimmed files apart', () => {
    const { groups, skimmed } = storyFiles(files, story)

    expect(groups.map((g) => [g.title, g.files.map((f) => f.path)])).toEqual([
      ['User table', ['src/Data/User.cs']],
      ['Login', ['src/Api/Login.cs', 'web/src/Login.tsx']],
      ['Other changes', ['README.md']],
    ])
    expect(skimmed.map((f) => f.path)).toEqual(['package-lock.json'])
  })

  it('moves through the story order with j/k and to the next unviewed file', () => {
    const order = fileOrder(files, story)
    expect(order).toEqual([
      'src/Data/User.cs',
      'src/Api/Login.cs',
      'web/src/Login.tsx',
      'README.md',
      'package-lock.json',
    ])
    expect(fileOrder(files)[0]).toBe('README.md')
    expect(nextUnviewed(files, 'src/Data/User.cs', new Set(['src/Data/User.cs']), order)).toBe(
      'src/Api/Login.cs',
    )
  })

  it('badges only medium and high risks', () => {
    expect(riskOf(story, 'src/Api/Login.cs')?.risk).toBe('high')
    expect(riskOf(story, 'README.md')).toBeNull()
    expect(riskOf(null, 'src/Api/Login.cs')).toBeNull()
  })

  it('puts the agent claim next to the analysis and marks disagreements', () => {
    const rows = coverageRows(
      [
        { id: 'c1', text: 'Logs in' },
        { id: 'c2', text: 'Has a test' },
        { id: 'c3', text: 'Not mentioned' },
      ],
      [
        { criterionId: 'c1', status: 'covered', note: null },
        { criterionId: 'c2', status: 'covered', note: 'LoginTests' },
      ],
      story,
    )

    expect(rows.map((r) => [r.text, r.claimed, r.found?.status ?? null, r.disagree])).toEqual([
      ['Logs in', 'covered', 'covered', false],
      ['Has a test', 'covered', 'missing', true],
      ['Not mentioned', null, null, false],
    ])
  })

  it('writes refs as path and line range', () => {
    expect(refText({ path: 'src/a.cs', lineStart: 3, lineEnd: 9 })).toBe('src/a.cs:3–9')
    expect(refText({ path: 'src/a.cs', lineStart: 3, lineEnd: 3 })).toBe('src/a.cs:3')
  })

  it('remembers the order per browser and falls back to story order', () => {
    const store = new Map<string, string>()
    const storage = {
      getItem: (k: string) => store.get(k) ?? null,
      setItem: (k: string, v: string) => void store.set(k, v),
    }
    expect(loadOrderMode(storage)).toBe('story')
    saveOrderMode('path', storage)
    expect(loadOrderMode(storage)).toBe('path')
    const broken = {
      getItem: () => {
        throw new Error('blocked')
      },
      setItem: () => {
        throw new Error('blocked')
      },
    }
    expect(loadOrderMode(broken)).toBe('story')
    expect(() => saveOrderMode('path', broken)).not.toThrow()
  })
})
