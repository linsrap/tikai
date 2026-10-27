import { retryDelay, staleKeys } from './live'

describe('live updates', () => {
  it('tries again at once, then slower, then every 30 s for good', () => {
    expect([0, 1, 2, 3, 4, 50].map(retryDelay)).toEqual([0, 2000, 5000, 10000, 30000, 30000])
  })

  it('re-reads the board, the ticket, its runs and the inbox when a run changes', () => {
    expect(staleKeys('RunChanged', { projectSlug: 'hub', ticketNumber: 7 })).toEqual([
      ['runs'],
      ['projects', 'hub', 'tickets', 7, 'runs'],
      ['projects', 'hub', 'tickets'],
      ['attention-items'],
    ])
  })

  it('re-reads the inbox and what runs wait for when an item changes', () => {
    expect(staleKeys('AttentionChanged', { projectSlug: 'hub', ticketNumber: 7 })).toEqual([
      ['attention-items'],
      ['projects', 'hub', 'tickets', 7, 'runs'],
      ['runs'],
    ])
  })
})
