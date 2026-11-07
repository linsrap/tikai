import { act, render } from '@testing-library/react'
import type { ReactNode } from 'react'
import { formatTitle, usePageTitle, useWaitingInTitle } from './title'

function Shell({ count, children }: { count: number; children: ReactNode }) {
  useWaitingInTitle(count)
  return <>{children}</>
}

function Page({ title }: { title: string }) {
  usePageTitle(title)
  return <p>{title}</p>
}

/** Every value written to document.title while `run` runs, after its microtasks. */
async function titleWrites(run: () => void): Promise<string[]> {
  const writes: string[] = []
  const setTitle = Object.getOwnPropertyDescriptor(Document.prototype, 'title')?.set
  const spy = vi.spyOn(document, 'title', 'set').mockImplementation((value: string) => {
    writes.push(value)
    setTitle?.call(document, value)
  })
  await act(async () => {
    run()
    await Promise.resolve()
  })
  spy.mockRestore()
  return writes
}

describe('formatTitle', () => {
  it('puts the page first and the app name last', () => {
    expect(formatTitle('Inbox', 0)).toBe('Inbox · Tikai')
    expect(formatTitle('TIK-12 Ticket title', 0)).toBe('TIK-12 Ticket title · Tikai')
    expect(formatTitle('Review TIK-12', 0)).toBe('Review TIK-12 · Tikai')
  })

  it('adds the waiting count only when it is above 0', () => {
    expect(formatTitle('Inbox', 2)).toBe('(2) Inbox · Tikai')
    expect(formatTitle('Inbox', 0)).toBe('Inbox · Tikai')
    expect(formatTitle(null, 0)).toBe('Tikai')
    expect(formatTitle(null, 3)).toBe('(3) Tikai')
  })
})

describe('title hooks', () => {
  afterEach(() => {
    document.title = ''
  })

  it('sets the title from the page and the count', async () => {
    await titleWrites(() => {
      render(
        <Shell count={2}>
          <Page title="Inbox" />
        </Shell>,
      )
    })
    expect(document.title).toBe('(2) Inbox · Tikai')
  })

  it('updates the count live and drops it at 0', async () => {
    const view = render(
      <Shell count={1}>
        <Page title="Inbox" />
      </Shell>,
    )
    await act(() => Promise.resolve())
    expect(document.title).toBe('(1) Inbox · Tikai')

    await titleWrites(() =>
      view.rerender(
        <Shell count={3}>
          <Page title="Inbox" />
        </Shell>,
      ),
    )
    expect(document.title).toBe('(3) Inbox · Tikai')

    await titleWrites(() =>
      view.rerender(
        <Shell count={0}>
          <Page title="Inbox" />
        </Shell>,
      ),
    )
    expect(document.title).toBe('Inbox · Tikai')
  })

  it('goes straight to the next page title when a navigation remounts page and shell', async () => {
    const view = render(
      <Shell key="a" count={2}>
        <Page title="Inbox" />
      </Shell>,
    )
    await act(() => Promise.resolve())

    const writes = await titleWrites(() =>
      view.rerender(
        <Shell key="b" count={2}>
          <Page title="TIK-12 Ticket title" />
        </Shell>,
      ),
    )
    expect(writes).toEqual(['(2) TIK-12 Ticket title · Tikai'])
  })

  it('falls back to the app name when no page sets a title', async () => {
    const view = render(<Page title="Settings" />)
    await act(() => Promise.resolve())
    await titleWrites(() => view.unmount())
    expect(document.title).toBe('Tikai')
  })
})
