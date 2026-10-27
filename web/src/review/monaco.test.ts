const config = vi.fn()
const bundled = { editor: {}, languages: {} }

vi.mock('@monaco-editor/react', () => ({ loader: { config } }))
vi.mock('monaco-editor/editor/editor.api', () => bundled)
vi.mock('monaco-editor/basic-languages/monaco.contribution', () => ({}))
vi.mock('monaco-editor/editor/editor.worker?worker', () => ({ default: vi.fn() }))

describe('monaco', () => {
  it('hands the bundled editor to the React wrapper, so nothing is loaded from a CDN', async () => {
    await import('./monaco')

    expect(config).toHaveBeenCalledTimes(1)
    const { monaco } = config.mock.calls[0][0] as { monaco: Record<string, unknown> }
    expect(monaco.editor).toBe(bundled.editor)
    expect(Object.keys(config.mock.calls[0][0] as object)).toEqual(['monaco'])
    expect(typeof self.MonacoEnvironment?.getWorker).toBe('function')
  })
})
