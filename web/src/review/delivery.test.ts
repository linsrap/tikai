import { approveAction, deliveryLine, type ChangeSet, type Delivery } from './review'

const changeSet = (
  delivery: Partial<Delivery>,
  status: ChangeSet['status'] = 'open',
): Pick<ChangeSet, 'status' | 'defaultBranch' | 'branch' | 'delivery'> => ({
  status,
  defaultBranch: 'main',
  branch: 'tikai/HUB-1-abc',
  delivery: {
    mode: 'merge',
    mergeStyle: 'squash',
    hasRemote: false,
    sha: null,
    error: null,
    conflict: null,
    ...delivery,
  },
})

describe('delivery', () => {
  it('says on the button what Approve does', () => {
    expect(approveAction(changeSet({}))).toBe('Approve and squash into main')
    expect(approveAction(changeSet({ hasRemote: true, mergeStyle: 'merge_commit' }))).toBe(
      'Approve and merge into main, then push',
    )
    expect(approveAction(changeSet({ hasRemote: true, mode: 'push_branch' }))).toBe(
      'Approve and push tikai/HUB-1-abc',
    )
    // Without a remote there is nowhere to push the branch to: the hub merges.
    expect(approveAction(changeSet({ mode: 'push_branch' }))).toBe('Approve and squash into main')
  })

  it('tells where an approved change is, and nothing before', () => {
    expect(deliveryLine(changeSet({}))).toBeNull()
    expect(deliveryLine(changeSet({ hasRemote: true }, 'delivering'))).toBe(
      'Approved. Merging into main and pushing…',
    )
    expect(deliveryLine(changeSet({ hasRemote: true, mode: 'push_branch' }, 'delivering'))).toBe(
      'Approved. Pushing tikai/HUB-1-abc…',
    )
    expect(deliveryLine(changeSet({ sha: '0123456789abcdef' }, 'merged'))).toBe(
      'Merged into main as 01234567.',
    )
    expect(
      deliveryLine(
        changeSet({ sha: '0123456789abcdef', mode: 'push_branch', hasRemote: true }, 'pushed'),
      ),
    ).toBe('Pushed tikai/HUB-1-abc at 01234567; merge it on the remote.')
  })
})
