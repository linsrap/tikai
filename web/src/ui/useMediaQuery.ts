import { useSyncExternalStore } from 'react'

/** Whether a media query matches, e.g. Tailwind's `lg` breakpoint; false where `matchMedia` is missing (tests). */
export function useMediaQuery(query: string): boolean {
  return useSyncExternalStore(
    (onChange) => {
      const list = globalThis.matchMedia?.(query)
      list?.addEventListener('change', onChange)
      return () => list?.removeEventListener('change', onChange)
    },
    () => globalThis.matchMedia?.(query).matches ?? false,
    () => false,
  )
}
