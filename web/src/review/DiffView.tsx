import './monaco'
import { DiffEditor } from '@monaco-editor/react'
import { useSyncExternalStore } from 'react'
import type { DiffMode } from './review'

const darkScheme = globalThis.matchMedia?.('(prefers-color-scheme: dark)')

function useDarkScheme() {
  return useSyncExternalStore(
    (listener) => {
      darkScheme?.addEventListener('change', listener)
      return () => darkScheme?.removeEventListener('change', listener)
    },
    () => darkScheme?.matches ?? false,
  )
}

/**
 * A read-only Monaco diff of one file. Unchanged regions are folded; Monaco shows a control to expand them, which
 * gives the full file on demand.
 */
export default function DiffView({
  original,
  modified,
  language,
  mode,
}: {
  original: string
  modified: string
  language: string
  mode: DiffMode
}) {
  const dark = useDarkScheme()
  return (
    <div className="diff-editor" data-testid="diff-editor">
      <DiffEditor
        height="70vh"
        original={original}
        modified={modified}
        language={language}
        theme={dark ? 'vs-dark' : 'vs'}
        loading={<p className="muted">Loading the diff…</p>}
        options={{
          readOnly: true,
          originalEditable: false,
          renderSideBySide: mode === 'side-by-side',
          // Below this width Monaco switches to inline by itself.
          useInlineViewWhenSpaceIsLimited: true,
          automaticLayout: true,
          minimap: { enabled: false },
          scrollBeyondLastLine: false,
          hideUnchangedRegions: { enabled: true },
          renderOverviewRuler: false,
        }}
      />
    </div>
  )
}
