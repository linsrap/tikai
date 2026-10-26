import {
  emptyRepositoryForm,
  suggestKey,
  suggestSlug,
  toCreateRepositoryRequest,
  type RepositoryForm,
} from './projects'

describe('suggestSlug', () => {
  it.each([
    ['Tikai Web', 'tikai-web'],
    ['  Tikai   Hub!  ', 'tikai-hub'],
    ['Café Ölmühle', 'cafe-olmuhle'],
    ['API v2', 'api-v2'],
    ['***', ''],
  ])('%s → %s', (name, slug) => {
    expect(suggestSlug(name)).toBe(slug)
  })

  it('stays within 40 characters and does not end in a hyphen', () => {
    const slug = suggestSlug('a'.repeat(39) + ' b')
    expect(slug.length).toBeLessThanOrEqual(40)
    expect(slug.endsWith('-')).toBe(false)
  })
})

describe('suggestKey', () => {
  it.each([
    ['Tikai', 'TIK'],
    ['Tikai Web', 'TW'],
    ['tikai hub api', 'THA'],
    ['2026 Roadmap', 'ROA'],
    ['', ''],
  ])('%s → %s', (name, key) => {
    expect(suggestKey(name)).toBe(key)
  })
})

describe('toCreateRepositoryRequest', () => {
  const form = (changes: Partial<RepositoryForm>): RepositoryForm => ({
    ...emptyRepositoryForm,
    name: ' api ',
    ...changes,
  })

  it('sends only name and branch for a local repository', () => {
    expect(toCreateRepositoryRequest(form({ remoteUrl: 'ignored', token: 'ignored' }))).toEqual({
      name: 'api',
      defaultBranch: null,
    })
  })

  it('sends the token and an optional user name for an HTTPS remote', () => {
    const request = toCreateRepositoryRequest(
      form({
        source: 'remote',
        remoteUrl: ' https://github.com/o/r.git ',
        credentialKind: 'https_token',
        token: 'tok',
        privateKey: 'not sent',
      }),
    )
    expect(request).toEqual({
      name: 'api',
      remoteUrl: 'https://github.com/o/r.git',
      defaultBranch: null,
      credential: { kind: 'https_token', username: null, token: 'tok' },
    })
  })

  it('sends only the key for an SSH remote', () => {
    const request = toCreateRepositoryRequest(
      form({
        source: 'remote',
        remoteUrl: 'git@github.com:o/r.git',
        defaultBranch: 'develop',
        credentialKind: 'ssh_key',
        privateKey: 'KEY',
        token: 'not sent',
      }),
    )
    expect(request.credential).toEqual({ kind: 'ssh_key', privateKey: 'KEY' })
    expect(request.defaultBranch).toBe('develop')
  })

  it('sends no credential for a public remote', () => {
    const request = toCreateRepositoryRequest(
      form({ source: 'remote', remoteUrl: 'https://github.com/o/r.git' }),
    )
    expect(request.credential).toBeNull()
  })
})
