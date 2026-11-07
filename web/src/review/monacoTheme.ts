import type { Monaco } from '@monaco-editor/react'

/**
 * Monaco themes in Tikai's colours (src/styles/app.css): the editor sits on the surface colour, line numbers are
 * quiet, and added and removed lines use the ok and bad colours of the rest of the app. Defined before the first
 * editor mounts; defining them again is harmless.
 */
export const monacoTheme = { light: 'tikai-light', dark: 'tikai-dark' } as const

export const monacoFont = {
  fontFamily: "'JetBrains Mono Variable', ui-monospace, 'Cascadia Mono', Consolas, monospace",
  fontSize: 12.5,
  lineHeight: 20,
}

export function defineMonacoThemes(monaco: Monaco) {
  monaco.editor.defineTheme(monacoTheme.light, {
    base: 'vs',
    inherit: true,
    rules: [],
    colors: {
      'editor.background': '#ffffff',
      'editorGutter.background': '#ffffff',
      'editor.lineHighlightBackground': '#00000000',
      'editor.lineHighlightBorder': '#00000000',
      'editorLineNumber.foreground': '#9a9892',
      'editorLineNumber.activeForeground': '#45443f',
      'editor.selectionBackground': '#1f56c22e',
      'editor.inactiveSelectionBackground': '#1f56c21a',
      'diffEditor.insertedTextBackground': '#2e8a4e2e',
      'diffEditor.removedTextBackground': '#c0352b29',
      'diffEditor.insertedLineBackground': '#2e8a4e14',
      'diffEditor.removedLineBackground': '#c0352b12',
      'diffEditorGutter.insertedLineBackground': '#2e8a4e24',
      'diffEditorGutter.removedLineBackground': '#c0352b1f',
      'diffEditor.border': '#e6e5e1',
      'diffEditor.diagonalFill': '#e6e5e1',
      'diffEditor.unchangedRegionBackground': '#f4f3f0',
      'diffEditor.unchangedRegionForeground': '#5f5e5a',
      'diffEditor.unchangedCodeBackground': '#00000000',
      'scrollbarSlider.background': '#1c1c1a1f',
      'scrollbarSlider.hoverBackground': '#1c1c1a33',
      'scrollbarSlider.activeBackground': '#1c1c1a4d',
    },
  })
  monaco.editor.defineTheme(monacoTheme.dark, {
    base: 'vs-dark',
    inherit: true,
    rules: [],
    colors: {
      'editor.background': '#1f1f1d',
      'editorGutter.background': '#1f1f1d',
      'editor.lineHighlightBackground': '#00000000',
      'editor.lineHighlightBorder': '#00000000',
      'editorLineNumber.foreground': '#75746e',
      'editorLineNumber.activeForeground': '#d2d1cc',
      'editor.selectionBackground': '#84a9f540',
      'editor.inactiveSelectionBackground': '#84a9f526',
      'diffEditor.insertedTextBackground': '#3fa35f40',
      'diffEditor.removedTextBackground': '#e5534b3d',
      'diffEditor.insertedLineBackground': '#3fa35f1a',
      'diffEditor.removedLineBackground': '#e5534b17',
      'diffEditorGutter.insertedLineBackground': '#3fa35f2e',
      'diffEditorGutter.removedLineBackground': '#e5534b29',
      'diffEditor.border': '#2f2f2c',
      'diffEditor.diagonalFill': '#2f2f2c',
      'diffEditor.unchangedRegionBackground': '#1b1b1a',
      'diffEditor.unchangedRegionForeground': '#aeada7',
      'diffEditor.unchangedCodeBackground': '#00000000',
      'scrollbarSlider.background': '#ffffff1f',
      'scrollbarSlider.hoverBackground': '#ffffff33',
      'scrollbarSlider.activeBackground': '#ffffff4d',
    },
  })
}
