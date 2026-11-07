import { usePageTitle } from '../ui/title'

/** A route whose parameter cannot be a ticket or a review. */
export function Missing({ children }: { children: string }) {
  usePageTitle('Not found')
  return <p className="m-0 p-10 text-fg-muted">{children}</p>
}
