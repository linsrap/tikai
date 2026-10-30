import './monaco'
import { DiffEditor, type DiffOnMount } from '@monaco-editor/react'
import { useEffect, useRef, useSyncExternalStore } from 'react'
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

type Editor = Parameters<DiffOnMount>[0]

/** Lines of the new file to scroll to and select (from a ref of the analysis). */
export type RevealLines = { lineStart: number; lineEnd: number; n: number }

function revealIn(editor: Editor, lines: RevealLines) {
  const modified = editor.getModifiedEditor()
  const model = modified.getModel()
  if (!model) return
  const start = Math.min(lines.lineStart, model.getLineCount())
  const end = Math.min(Math.max(lines.lineEnd, start), model.getLineCount())
  modified.setSelection({
    startLineNumber: start,
    startColumn: 1,
    endLineNumber: end,
    endColumn: model.getLineMaxColumn(end),
  })
  modified.revealLinesInCenter(start, end)
}

/**
 * A read-only Monaco diff of one file. Unchanged regions are folded; Monaco shows a control to expand them, which
 * gives the full file on demand. A reveal shows the whole file (nothing folded) and selects the lines.
 */
export default function DiffView({
  original,
  modified,
  language,
  mode,
  reveal = null,
}: {
  original: string
  modified: string
  language: string
  mode: DiffMode
  reveal?: RevealLines | null
}) {
  const dark = useDarkScheme()
  const editor = useRef<Editor | null>(null)

  useEffect(() => {
    if (editor.current && reveal) revealIn(editor.current, reveal)
  }, [reveal])

  return (
    <div className="diff-editor" data-testid="diff-editor">
      <DiffEditor
        height="70vh"
        original={original}
        modified={modified}
        language={language}
        theme={dark ? 'vs-dark' : 'vs'}
        loading={<p className="muted">Loading the diff…</p>}
        onMount={(mounted) => {
          editor.current = mounted
          if (reveal) {
            revealIn(mounted, reveal)
            // The diff is computed after the mount and can move the lines; show them again once it is there.
            const once = mounted.onDidUpdateDiff(() => {
              once.dispose()
              revealIn(mounted, reveal)
            })
          }
        }}
        options={{
          readOnly: true,
          originalEditable: false,
          renderSideBySide: mode === 'side-by-side',
          // Below this width Monaco switches to inline by itself.
          useInlineViewWhenSpaceIsLimited: true,
          automaticLayout: true,
          minimap: { enabled: false },
          scrollBeyondLastLine: false,
          hideUnchangedRegions: { enabled: !reveal },
          renderOverviewRuler: false,
        }}
      />
    </div>
  )
}
