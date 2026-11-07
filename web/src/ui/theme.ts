import { useSyncExternalStore } from 'react'

/** The person's choice: follow the system, or always light or dark. Kept per browser. */
export type ThemeChoice = 'system' | 'light' | 'dark'

const key = 'tikai.theme'
const listeners = new Set<() => void>()
const darkQuery = globalThis.matchMedia?.('(prefers-color-scheme: dark)')

function read(): ThemeChoice {
  try {
    const value = localStorage.getItem(key)
    return value === 'light' || value === 'dark' ? value : 'system'
  } catch {
    return 'system'
  }
}

let choice: ThemeChoice = read()

/** Sets data-theme on <html> (app.css switches its colours on it); none for "system". */
export function applyTheme(next: ThemeChoice = choice) {
  const root = document.documentElement
  if (next === 'system') delete root.dataset.theme
  else root.dataset.theme = next
}

export function setTheme(next: ThemeChoice) {
  choice = next
  try {
    if (next === 'system') localStorage.removeItem(key)
    else localStorage.setItem(key, next)
  } catch {
    // Storage can be blocked; the choice still holds for this page.
  }
  applyTheme(next)
  listeners.forEach((listener) => listener())
}

function subscribe(listener: () => void) {
  listeners.add(listener)
  darkQuery?.addEventListener('change', listener)
  return () => {
    listeners.delete(listener)
    darkQuery?.removeEventListener('change', listener)
  }
}

export const useThemeChoice = () => useSyncExternalStore(subscribe, () => choice)

/** Whether the page is dark now: the choice, or the system setting for "system". */
export const useDarkMode = () =>
  useSyncExternalStore(subscribe, () =>
    choice === 'system' ? (darkQuery?.matches ?? false) : choice === 'dark',
  )
