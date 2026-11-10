import { tryLocally } from './review'

test('a round becomes a download and two commands for a branch and a worktree', () => {
  const tried = tryLocally({ id: 'abc', ticketKey: 'TIKAI-12', repositoryName: 'tikai' }, 2)
  expect(tried.url).toBe('/api/change-sets/abc/rounds/2/bundle')
  expect(tried.fileName).toBe('TIKAI-12-round-2.bundle')
  expect(tried.commands).toEqual([
    'git fetch $HOME/Downloads/TIKAI-12-round-2.bundle +refs/tikai/rounds/abc/2:refs/heads/review/TIKAI-12-r2',
    'git worktree add ../tikai-TIKAI-12-r2 review/TIKAI-12-r2',
  ])
})

test('the worktree folder name is safe for any repository name', () => {
  const tried = tryLocally({ id: 'abc', ticketKey: 'HUB-1', repositoryName: 'My App (old)' }, 1)
  expect(tried.commands[1]).toBe('git worktree add ../My-App--old-HUB-1-r1 review/HUB-1-r1')
})
