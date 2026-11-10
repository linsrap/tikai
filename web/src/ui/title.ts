import { useLayoutEffect } from 'react'

const appName = 'Tikai'

/** `(2) Inbox · Tikai`: the waiting count only when there is one, the app name always last. */
export function formatTitle(page: string | null, waiting: number): string {
  const base = page ? `${page} · ${appName}` : appName
  return waiting > 0 ? `(${waiting}) ${base}` : base
}

// The page and the shell each own one part of the title. Changes are written once per commit, in a microtask (before
// the browser paints): a navigation unmounts one page and shell and mounts the next, and the steps in between (no
// page, no count) never reach the tab.
let page: string | null = null
let waiting = 0
let scheduled = false

function write() {
  if (scheduled) return
  scheduled = true
  queueMicrotask(() => {
    scheduled = false
    const title = formatTitle(page, waiting)
    if (document.title !== title) document.title = title
  })
}

/**
 * Sets the page's part of the browser tab title, in the same frame as the page (the routes load the data a title needs
 * before the page renders: no default or previous title in between).
 */
export function usePageTitle(title: string) {
  useLayoutEffect(() => {
    page = title
    write()
    return () => {
      page = null
      write()
    }
  }, [title])
}

/** Puts the inbox's waiting count in front of the title; the shell of the logged-in pages calls it. */
export function useWaitingInTitle(count: number) {
  useLayoutEffect(() => {
    waiting = count
    write()
    return () => {
      waiting = 0
      write()
    }
  }, [count])
}
