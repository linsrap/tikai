// Monaco from the bundle, never from a CDN: @monaco-editor/react loads it from jsDelivr unless it is handed an
// instance. Only the editor core with the highlighting grammars and the editor worker (diffing): a read-only diff
// needs no language services, which would want workers of their own.
import { loader } from '@monaco-editor/react'
import * as monaco from 'monaco-editor/editor/editor.api'
import 'monaco-editor/basic-languages/monaco.contribution'
import EditorWorker from 'monaco-editor/editor/editor.worker?worker'

self.MonacoEnvironment = { getWorker: () => new EditorWorker() }
loader.config({ monaco })

export { monaco }
