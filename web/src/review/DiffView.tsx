import './monaco'
import { DiffEditor, type DiffOnMount } from '@monaco-editor/react'
import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { useDarkMode } from '../ui/theme'
import { defineMonacoThemes, monacoFont, monacoTheme } from './monacoTheme'
import type { DiffMode, Pane } from './review'

type Editor = Parameters<DiffOnMount>[0]
type CodeEditor = ReturnType<Editor['getModifiedEditor']>

/** Lines of the new file to scroll to and select (from a ref of the analysis). */
export type RevealLines = { lineStart: number; lineEnd: number; n: number }

/** Something shown between the lines of one pane, e.g. a comment thread under the lines it is about. */
export type DiffZone = { key: string; pane: Pane; afterLine: number; content: ReactNode }

/** Lines selected in a pane (a cursor alone selects its line). */
export type LineSelection = { pane: Pane; lineStart: number; lineEnd: number }

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
 * gives the full file on demand. A reveal shows the whole file (nothing folded) and selects the lines. Zones (comment
 * threads, a comment form) show between the lines of a pane; with zones nothing is folded, so none hides.
 */
export default function DiffView({
  original,
  modified,
  language,
  mode,
  reveal = null,
  zones = [],
  onSelection,
}: {
  original: string
  modified: string
  language: string
  mode: DiffMode
  reveal?: RevealLines | null
  zones?: readonly DiffZone[]
  onSelection?: (selection: LineSelection | null) => void
}) {
  const dark = useDarkMode()
  const editor = useRef<Editor | null>(null)
  const [panes, setPanes] = useState<Record<Pane, CodeEditor> | null>(null)
  const selectionListener = useRef(onSelection)
  useEffect(() => {
    selectionListener.current = onSelection
  })

  useEffect(() => {
    if (editor.current && reveal) revealIn(editor.current, reveal)
  }, [reveal])

  return (
    <div
      className="overflow-hidden rounded-b-lg border border-t-0 border-line"
      data-testid="diff-editor"
    >
      <DiffEditor
        height="70vh"
        original={original}
        modified={modified}
        language={language}
        theme={dark ? monacoTheme.dark : monacoTheme.light}
        loading={<p className="m-0 p-4 text-sm text-fg-subtle">Loading the diff…</p>}
        beforeMount={defineMonacoThemes}
        onMount={(mounted, monaco) => {
          editor.current = mounted
          // Monaco measures the font when it mounts; measure again once the bundled mono font is loaded.
          void document.fonts
            ?.load(`${monacoFont.fontSize}px ${monacoFont.fontFamily}`)
            .then(() => monaco.editor.remeasureFonts())
          const both = {
            original: mounted.getOriginalEditor(),
            modified: mounted.getModifiedEditor(),
          }
          setPanes(both)
          for (const pane of ['original', 'modified'] as const) {
            both[pane].onDidChangeCursorSelection(({ selection }) => {
              if (!both[pane].hasTextFocus()) return
              // A selection that ends at the start of a line does not take that line.
              const end =
                selection.endColumn === 1 && selection.endLineNumber > selection.startLineNumber
                  ? selection.endLineNumber - 1
                  : selection.endLineNumber
              selectionListener.current?.({
                pane,
                lineStart: selection.startLineNumber,
                lineEnd: end,
              })
            })
          }
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
          // Below this width Monaco switches to inline by itself (its default, 900 px, is too wide next to the
          // file tree on a laptop screen).
          useInlineViewWhenSpaceIsLimited: true,
          renderSideBySideInlineBreakpoint: 700,
          automaticLayout: true,
          minimap: { enabled: false },
          scrollBeyondLastLine: false,
          hideUnchangedRegions: { enabled: !reveal && zones.length === 0 },
          renderOverviewRuler: false,
          ...monacoFont,
          padding: { top: 8 },
        }}
      />
      {panes &&
        zones.map((zone) => (
          <Zone key={zone.key} editor={panes[zone.pane]} afterLine={zone.afterLine}>
            {zone.content}
          </Zone>
        ))}
    </div>
  )
}

/**
 * The DOM side of a zone: a view zone keeps the space between the lines (it grows with the content), and an overlay
 * widget at the zone's top holds the content, as VS Code's own comment widget does it. So the content's fields and
 * buttons get mouse and keyboard input like any other part of the page.
 */
class ZoneHost {
  readonly node = document.createElement('div')

  constructor() {
    this.node.className = 'diff-zone z-[5] font-sans text-sm leading-5'
    this.node.style.position = 'absolute'
  }

  /** Shows the node after the line; returns what removes it again. */
  attach(editor: CodeEditor, afterLine: number): () => void {
    const node = this.node
    const zone = {
      afterLineNumber: afterLine,
      heightInPx: 48,
      domNode: document.createElement('div'),
      onDomNodeTop: (top: number) => {
        node.style.top = `${top}px`
      },
    }
    let zoneId = ''
    editor.changeViewZones((zones) => {
      zoneId = zones.addZone(zone)
    })
    const widget = {
      getId: () => `tikai.zone.${zoneId}`,
      getDomNode: () => node,
      getPosition: () => null,
    }
    editor.addOverlayWidget(widget)
    const place = () => {
      const layout = editor.getLayoutInfo()
      node.style.left = `${layout.contentLeft}px`
      node.style.width = `${Math.max(layout.contentWidth - layout.verticalScrollbarWidth - 8, 120)}px`
    }
    place()
    const layoutChange = editor.onDidLayoutChange(place)
    const resize = new ResizeObserver(() => {
      const height = node.offsetHeight + 8
      if (height !== zone.heightInPx) {
        zone.heightInPx = height
        editor.changeViewZones((zones) => zones.layoutZone(zoneId))
      }
    })
    resize.observe(node)
    return () => {
      resize.disconnect()
      layoutChange.dispose()
      editor.removeOverlayWidget(widget)
      editor.changeViewZones((zones) => zones.removeZone(zoneId))
    }
  }
}

/** React content between the lines of a pane (see ZoneHost). */
function Zone({
  editor,
  afterLine,
  children,
}: {
  editor: CodeEditor
  afterLine: number
  children: ReactNode
}) {
  const [host] = useState(() => new ZoneHost())
  useLayoutEffect(() => host.attach(editor, afterLine), [host, editor, afterLine])
  return createPortal(children, host.node)
}
