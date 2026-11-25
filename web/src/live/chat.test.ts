import { describe, expect, it } from 'vitest'
import type { RunEventInfo } from '../runs/runs'
import {
  ChatBuilder,
  diffHunks,
  diffStat,
  lineDiff,
  relativePath,
  toolTitle,
  type ChatItem,
  type ToolItem,
} from './chat'

let seq = 0
const event = (kind: RunEventInfo['kind'], payload: Record<string, unknown>): RunEventInfo => ({
  seq: ++seq,
  kind,
  payload: payload as RunEventInfo['payload'],
  createdAt: '2026-11-25T12:00:00Z',
})

const init = (cwd = '/run/work') =>
  event('system', {
    subtype: 'init',
    cwd,
    model: 'claude-opus-5-5',
    claude_code_version: '2.1.310',
    mcp_servers: [{ name: 'tikai', status: 'connected' }],
  })
const say = (text: string, parent?: string) =>
  event('message', { role: 'assistant', text, ...(parent ? { parent_tool_use_id: parent } : {}) })
const call = (id: string, name: string, input: Record<string, unknown>, parent?: string) =>
  event('tool_call', { id, name, input, parent_tool_use_id: parent ?? null })
const result = (id: string, content: string, isError = false) =>
  event('tool_result', { tool_use_id: id, content, is_error: isError })

const build = (events: RunEventInfo[]) => {
  const chat = new ChatBuilder()
  chat.update(events)
  return chat
}
const tools = (items: ChatItem[]) => items.filter((i): i is ToolItem => i.kind === 'tool')

describe('ChatBuilder', () => {
  it('pairs a call with its result and keeps text, thinking and the session', () => {
    const chat = build([
      init(),
      event('message', { role: 'assistant', thinking: 'Let me look.' }),
      say('Reading the code.'),
      call('t1', 'Bash', { command: 'ls' }),
      result('t1', 'a.txt'),
    ])

    expect(chat.items.map((i) => i.kind)).toEqual(['session', 'thinking', 'text', 'tool'])
    expect(chat.items[0]).toMatchObject({
      text: 'Agent session started · claude-opus-5-5 · Claude Code 2.1.310',
      problem: false,
    })
    expect(tools(chat.items)[0].result).toEqual({
      content: 'a.txt',
      isError: false,
      truncated: false,
    })
    expect(chat.cwd).toBe('/run/work')
  })

  it('shows a call without a result as running, and an error result as failed', () => {
    const events = [call('t1', 'Bash', { command: 'false' }), call('t2', 'Read', {})]
    const chat = build(events)
    events.push(result('t1', 'boom', true))
    chat.update(events)

    const [first, second] = tools(chat.items)
    expect(first.result?.isError).toBe(true)
    expect(second.result).toBeNull()
    expect(second.interrupted).toBe(false)
  })

  it('takes a result that arrives before its call', () => {
    const chat = build([result('t1', 'early'), call('t1', 'Bash', { command: 'ls' })])

    expect(tools(chat.items)[0].result?.content).toBe('early')
  })

  it('marks calls without a result as interrupted when the session ends or a new one starts', () => {
    const chat = build([
      init(),
      call('t1', 'Bash', { command: 'sleep 999' }),
      event('result', { subtype: 'error_during_execution', is_error: true, num_turns: 3 }),
      init(),
      call('t2', 'Read', { file_path: '/run/work/a.txt' }),
    ])

    const [first, second] = tools(chat.items)
    expect(first.interrupted).toBe(true)
    expect(second.interrupted).toBe(false)
    expect(chat.items.find((i) => i.kind === 'end')).toMatchObject({
      isError: true,
      text: 'Agent ended with an error (error_during_execution) · 3 turns',
    })
    expect(chat.items.filter((i) => i.kind === 'session')).toHaveLength(2)
  })

  it('nests what a subagent did under its Task call', () => {
    const chat = build([
      call('task', 'Task', { description: 'Find the tests', prompt: 'Look around.' }),
      say('Searching.', 'task'),
      call('t2', 'Grep', { pattern: 'describe' }, 'task'),
      result('t2', 'a.test.ts'),
      result('task', 'Found one file.'),
      say('Done.'),
    ])

    expect(chat.items.map((i) => i.kind)).toEqual(['tool', 'text'])
    const task = tools(chat.items)[0]
    expect(task.children.map((i) => i.kind)).toEqual(['text', 'tool'])
    expect(tools(task.children)[0].result?.content).toBe('a.test.ts')
    expect(task.result?.content).toBe('Found one file.')
  })

  it('appending events one by one builds the same chat as all at once', () => {
    const events = [
      init(),
      say('Hi.'),
      call('t1', 'Bash', { command: 'ls' }),
      result('t1', 'x'),
      event('progress', { step: 'Writing' }),
      event('log', { source: 'hub', text: 'Resuming' }),
      event('result', { subtype: 'success', is_error: false, num_turns: 2, total_cost_usd: 0.5 }),
    ]
    const live = new ChatBuilder()
    for (let n = 1; n <= events.length; n++) live.update(events.slice(0, n))

    expect(live.items).toEqual(build(events).items)
    expect(live.update(events)).toBe(false)
  })

  it('drops progress events (the call shows them) and empty text, and keeps log lines as notes', () => {
    const chat = build([
      event('progress', { step: 'Writing' }),
      say('  '),
      event('log', { source: 'hub', text: 'Resuming in a new container' }),
    ])

    expect(chat.items).toEqual([
      expect.objectContaining({ kind: 'note', text: 'Hub: Resuming in a new container' }),
    ])
  })

  it('shows an event cut to a flat preview as a note', () => {
    const chat = build([
      event('tool_call', { truncated: true, preview: '{"id":"t1","name":"Write"' }),
    ])

    expect(chat.items[0]).toMatchObject({ kind: 'note' })
    expect((chat.items[0] as { text: string }).text).toContain('Event too large')
  })

  it('names a session whose Tikai tools are not connected', () => {
    const chat = build([
      event('system', { subtype: 'init', mcp_servers: [{ name: 'tikai', status: 'failed' }] }),
    ])

    expect(chat.items[0]).toMatchObject({ problem: true })
    expect((chat.items[0] as { text: string }).text).toContain('Tikai tools failed')
  })
})

describe('toolTitle', () => {
  it('names the tool and what it worked on', () => {
    const t = (name: string, input: Record<string, unknown>) =>
      toolTitle({ name, input }, '/run/work')

    expect(t('Bash', { command: 'npm test', description: 'Run the tests' })).toEqual({
      label: 'Bash',
      detail: 'Run the tests',
    })
    expect(t('Bash', { command: 'npm test' }).detail).toBe('npm test')
    expect(t('Read', { file_path: '/run/work/src/a.ts', offset: 10, limit: 5 }).detail).toBe(
      'src/a.ts (lines 10–14)',
    )
    expect(t('Edit', { file_path: '/etc/hosts' }).detail).toBe('/etc/hosts')
    expect(t('Grep', { pattern: 'foo', path: '/run/work/src' }).detail).toBe('foo in src')
    expect(t('TodoWrite', { todos: [{ status: 'completed' }, { status: 'pending' }] })).toEqual({
      label: 'Todos',
      detail: '1/2 done',
    })
    expect(t('mcp__tikai__ask_human', { question: 'Which word?' })).toEqual({
      label: 'Asked a person',
      detail: 'Which word?',
    })
    expect(t('mcp__tikai__new_tool', {}).label).toBe('new tool')
    expect(t('mcp__other__x', { a: 1 })).toEqual({ label: 'mcp__other__x', detail: '{"a":1}' })
  })

  it('shortens only paths under the working directory', () => {
    expect(relativePath('/run/work/a.ts', '/run/work')).toBe('a.ts')
    expect(relativePath('/run/workshop/a.ts', '/run/work')).toBe('/run/workshop/a.ts')
    expect(relativePath('/run/work/a.ts', '')).toBe('/run/work/a.ts')
  })
})

describe('lineDiff', () => {
  it('keeps common lines and puts removed before added lines in a change', () => {
    const diff = lineDiff('a\nb\nc\nd', 'a\nB\nc\nd\ne')

    expect(diff).toEqual([
      { op: ' ', text: 'a' },
      { op: '-', text: 'b' },
      { op: '+', text: 'B' },
      { op: ' ', text: 'c' },
      { op: ' ', text: 'd' },
      { op: '+', text: 'e' },
    ])
    expect(diffStat(diff)).toEqual({ added: 2, removed: 1 })
  })

  it('shows a very large change as removed then added lines', () => {
    const before = Array.from({ length: 600 }, (_, i) => `old ${i}`).join('\n')
    const after = Array.from({ length: 600 }, (_, i) => `new ${i}`).join('\n')

    const diff = lineDiff(before, after)

    expect(diff).toHaveLength(1200)
    expect(diff[0].op).toBe('-')
    expect(diff[1199].op).toBe('+')
  })

  it('folds long unchanged stretches into gaps', () => {
    const lines = Array.from({ length: 20 }, (_, i) => `line ${i}`)
    const changed = [...lines]
    changed[10] = 'changed'

    const hunks = diffHunks(lineDiff(lines.join('\n'), changed.join('\n')))

    expect(hunks[0]).toEqual({ op: 'gap', count: 7 })
    expect(hunks.filter((h) => h.op === ' ')).toHaveLength(6)
    expect(hunks.at(-1)).toEqual({ op: 'gap', count: 6 })
  })

  it('has no hunks without a change', () => {
    expect(diffHunks(lineDiff('a\nb', 'a\nb'))).toEqual([])
  })
})
