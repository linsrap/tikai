import { lazy, Suspense } from 'react'

// The markdown parser is most of a ticket page's code; loading it on demand keeps it out of the main bundle.
const Markdown = lazy(() => import('./Markdown').then((m) => ({ default: m.Markdown })))

/** Ticket markdown, sanitized (see Markdown). */
export function MarkdownView({ text }: { text: string }) {
  return (
    <Suspense fallback={<p className="muted">Loading…</p>}>
      <Markdown text={text} />
    </Suspense>
  )
}
