import {
  fileOrder,
  groupFiles,
  lineCounts,
  loadDiffMode,
  nextUnviewed,
  saveDiffMode,
  sizeText,
  startsCollapsed,
  stepFile,
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
  file('web/src/b.ts'),
  file('README.md'),
  file('package-lock.json', { lock: true }),
  file('src/Api/Program.cs'),
  file('web/src/a.ts'),
  file('src/Data/Migrations/X.Designer.cs', { generated: true }),
  file('LICENSE'),
]

describe('review files', () => {
  it('groups by directory, root first, and keeps generated and lock files apart', () => {
    const { groups, skimmed } = groupFiles(files)

    expect(groups.map((g) => [g.directory, g.files.map((f) => f.path)])).toEqual([
      ['', ['LICENSE', 'README.md']],
      ['src/Api', ['src/Api/Program.cs']],
      ['web/src', ['web/src/a.ts', 'web/src/b.ts']],
    ])
    expect(skimmed.map((f) => f.path)).toEqual([
      'package-lock.json',
      'src/Data/Migrations/X.Designer.cs',
    ])
  })

  it('moves through the tree from top to bottom and wraps around', () => {
    const order = fileOrder(files)

    expect(order).toEqual([
      'LICENSE',
      'README.md',
      'src/Api/Program.cs',
      'web/src/a.ts',
      'web/src/b.ts',
      'package-lock.json',
      'src/Data/Migrations/X.Designer.cs',
    ])
    expect(stepFile(order, 'README.md', 1)).toBe('src/Api/Program.cs')
    expect(stepFile(order, 'LICENSE', -1)).toBe('src/Data/Migrations/X.Designer.cs')
    expect(stepFile(order, 'src/Data/Migrations/X.Designer.cs', 1)).toBe('LICENSE')
    expect(stepFile(order, null, 1)).toBe('LICENSE')
    expect(stepFile([], null, 1)).toBeNull()
  })

  it('finds the next file still to view, counting what was just marked', () => {
    const some = files.map((f) => (f.path === 'src/Api/Program.cs' ? { ...f, viewed: true } : f))

    expect(nextUnviewed(some, 'README.md', new Set(['README.md']))).toBe('web/src/a.ts')
    expect(nextUnviewed(some, 'src/Data/Migrations/X.Designer.cs')).toBe('LICENSE')
    const all = files.map((f) => ({ ...f, viewed: true }))
    expect(nextUnviewed(all, 'LICENSE')).toBeNull()
    expect(nextUnviewed([file('only')], 'only', new Set(['only']))).toBeNull()
  })

  it('collapses what a reviewer skims or cannot read', () => {
    expect(startsCollapsed(file('a.ts'))).toBe(false)
    expect(startsCollapsed(file('yarn.lock', { lock: true }))).toBe(true)
    expect(startsCollapsed(file('big.txt', { large: true }))).toBe(true)
    expect(startsCollapsed(file('a.png', { binary: true, additions: null, deletions: null }))).toBe(
      true,
    )
  })

  it('writes line counts and sizes', () => {
    expect(lineCounts(file('a', { additions: 12, deletions: 3 }))).toBe('+12 −3')
    expect(lineCounts(file('a', { additions: null, deletions: null }))).toBe('binary')
    expect(sizeText(12)).toBe('12 bytes')
    expect(sizeText(340 * 1024)).toBe('340 KB')
    expect(sizeText(2_100_000)).toBe('2.0 MB')
  })
})

describe('diff mode', () => {
  it('is remembered, and side by side without storage or when storage throws', () => {
    const values = new Map<string, string>()
    const storage = {
      getItem: (k: string) => values.get(k) ?? null,
      setItem: (k: string, v: string) => void values.set(k, v),
    }
    expect(loadDiffMode(storage)).toBe('side-by-side')
    saveDiffMode('inline', storage)
    expect(loadDiffMode(storage)).toBe('inline')

    const broken = {
      getItem: () => {
        throw new Error('blocked')
      },
      setItem: () => {
        throw new Error('blocked')
      },
    }
    expect(loadDiffMode(broken)).toBe('side-by-side')
    expect(() => saveDiffMode('inline', broken)).not.toThrow()
    expect(loadDiffMode(undefined)).toBe('side-by-side')
  })
})
