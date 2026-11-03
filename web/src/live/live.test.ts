import { QueryClient, QueryObserver } from '@tanstack/react-query'
import { invalidateKeys, retryDelay, staleKeys } from './live'

describe('live updates', () => {
  it('re-reads a first load that was on its way when the change came, instead of keeping its old answer', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const answers: ((value: string) => void)[] = []
    const observer = new QueryObserver<string>(client, {
      queryKey: ['change-sets', 'cs1'],
      queryFn: () => new Promise<string>((resolve) => answers.push(resolve)),
    })
    const unsubscribe = observer.subscribe(() => {})
    await vi.waitFor(() => expect(answers).toHaveLength(1))

    // The server read "pending"; the change comes before its answer arrives.
    const invalidated = invalidateKeys(client, [['change-sets', 'cs1']])
    await new Promise((resolve) => setTimeout(resolve, 0))
    answers[0]('pending')
    await vi.waitFor(() => expect(answers).toHaveLength(2))
    answers[1]('done')
    await invalidated

    await vi.waitFor(() => expect(observer.getCurrentResult().data).toBe('done'))
    unsubscribe()
    client.clear()
  })

  it('tries again at once, then slower, then every 30 s for good', () => {
    expect([0, 1, 2, 3, 4, 50].map(retryDelay)).toEqual([0, 2000, 5000, 10000, 30000, 30000])
  })

  it('re-reads the board, the ticket, its runs and the inbox when a run changes', () => {
    expect(staleKeys('RunChanged', { projectSlug: 'hub', ticketNumber: 7 })).toEqual([
      ['runs'],
      ['projects', 'hub', 'tickets', 7, 'runs'],
      ['projects', 'hub', 'tickets'],
      ['attention-items'],
      ['change-sets'],
    ])
  })

  it('re-reads the review, its threads and the links to it when a change set changes, not its files', () => {
    const keys = staleKeys('ChangeSetChanged', {
      projectSlug: 'hub',
      ticketNumber: 7,
      changeSetId: 'cs1',
    })

    expect(keys).toEqual([
      ['change-sets', 'cs1'],
      ['change-set-threads', 'cs1'],
      ['projects', 'hub', 'tickets', 7, 'runs'],
      ['runs'],
      ['attention-items'],
    ])
    expect(keys.some((k) => k[0] === 'change-set-files' || k[0] === 'change-set-file')).toBe(false)
  })

  it('re-reads the inbox and what runs wait for when an item changes', () => {
    expect(staleKeys('AttentionChanged', { projectSlug: 'hub', ticketNumber: 7 })).toEqual([
      ['attention-items'],
      ['projects', 'hub', 'tickets', 7, 'runs'],
      ['runs'],
    ])
  })
})
