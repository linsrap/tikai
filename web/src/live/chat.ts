import { eventSummary, type RunEventInfo } from '../runs/runs'

type Payload = Record<string, unknown>

type Base = {
  /** Stable across updates: the seq of the event that made the item. */
  key: string
  seq: number
  at: string
}

export type ToolResult = { content: string; isError: boolean; truncated: boolean }

export type ToolItem = Base & {
  kind: 'tool'
  id: string
  name: string
  input: Payload
  /** The call's input was cut to fit (see RunEventPayload.Trim). */
  truncated: boolean
  /** Null while the tool runs. */
  result: ToolResult | null
  /** The agent's session ended before a result came (a stopped container, a resume). */
  interrupted: boolean
  /** What a subagent did inside this call (Task). */
  children: ChatItem[]
}

export type ChatItem =
  | (Base & { kind: 'text'; role: 'assistant' | 'user'; text: string })
  | (Base & { kind: 'thinking'; text: string })
  | ToolItem
  | (Base & { kind: 'session'; text: string; problem: boolean })
  | (Base & { kind: 'end'; text: string; isError: boolean })
  | (Base & { kind: 'note'; text: string; isError: boolean })

const str = (value: unknown) => (typeof value === 'string' ? value : '')
const num = (value: unknown) => (typeof value === 'number' ? value : null)

/**
 * Turns a run's events into a conversation, the way Claude Code's own chat shows the same stream: text, thinking,
 * one item per tool call with its result, a divider per agent session and the end. Events are added incrementally
 * (a live run grows by a few events at a time); items are changed in place when a result arrives, so callers render
 * again on every update instead of comparing items.
 */
export class ChatBuilder {
  readonly items: ChatItem[] = []
  /** The agent's working directory, from the last session start; paths under it are shown relative. */
  cwd = ''
  private consumed = 0
  private readonly calls = new Map<string, ToolItem>()
  /** Results that came before their call (not expected from Claude Code, but nothing is lost if they do). */
  private readonly early = new Map<string, ToolResult>()

  /** Adds the events after those already seen; the list only ever grows. Returns whether anything was added. */
  update(events: readonly RunEventInfo[]): boolean {
    if (events.length <= this.consumed) return false
    for (let i = this.consumed; i < events.length; i++) this.add(events[i])
    this.consumed = events.length
    return true
  }

  private add(event: RunEventInfo) {
    const p = (event.payload ?? {}) as Payload
    const base: Base = { key: String(event.seq), seq: event.seq, at: event.createdAt }
    if (p.preview !== undefined && p.truncated === true) {
      this.push(p, {
        ...base,
        kind: 'note',
        text: `Event too large to show (${event.kind}): ${str(p.preview).slice(0, 300)}`,
        isError: false,
      })
      return
    }
    switch (event.kind) {
      case 'system': {
        this.interruptOpenCalls()
        this.cwd = str(p.cwd)
        const servers = Array.isArray(p.mcp_servers) ? (p.mcp_servers as Payload[]) : []
        const tikai = servers.find((s) => s.name === 'tikai')
        const problem = str(tikai?.status) !== 'connected'
        const parts = [
          'Agent session started',
          str(p.model),
          p.claude_code_version ? `Claude Code ${str(p.claude_code_version)}` : '',
          problem ? `Tikai tools ${str(tikai?.status) || 'missing'}` : '',
        ]
        this.items.push({
          ...base,
          kind: 'session',
          text: parts.filter(Boolean).join(' · '),
          problem,
        })
        return
      }
      case 'message': {
        if (p.thinking !== undefined) {
          if (str(p.thinking).trim()) {
            this.push(p, { ...base, kind: 'thinking', text: str(p.thinking) })
          }
          return
        }
        if (str(p.text).trim()) {
          const role = p.role === 'user' ? 'user' : 'assistant'
          this.push(p, { ...base, kind: 'text', role, text: str(p.text) })
        }
        return
      }
      case 'tool_call': {
        const id = str(p.id)
        const item: ToolItem = {
          ...base,
          kind: 'tool',
          id,
          name: str(p.name),
          input: p.input && typeof p.input === 'object' ? (p.input as Payload) : {},
          truncated: p.truncated === true,
          result: this.early.get(id) ?? null,
          interrupted: false,
          children: [],
        }
        this.early.delete(id)
        if (id) this.calls.set(id, item)
        this.push(p, item)
        return
      }
      case 'tool_result': {
        const id = str(p.tool_use_id)
        const result: ToolResult = {
          content: str(p.content),
          isError: p.is_error === true,
          truncated: p.truncated === true,
        }
        const call = this.calls.get(id)
        if (call) call.result = result
        else this.early.set(id, result)
        return
      }
      case 'progress':
        // report_progress shows as its tool call already.
        return
      case 'result': {
        this.interruptOpenCalls()
        const turns = num(p.num_turns)
        const ms = num(p.duration_ms)
        const cost = num(p.total_cost_usd)
        const isError = p.is_error === true
        const parts = [
          isError ? `Agent ended with an error (${str(p.subtype) || 'unknown'})` : 'Agent finished',
          turns != null ? `${turns} turn${turns === 1 ? '' : 's'}` : '',
          ms != null ? duration(ms) : '',
          cost != null ? `${cost.toFixed(4)} USD so far` : '',
        ]
        this.items.push({ ...base, kind: 'end', text: parts.filter(Boolean).join(' · '), isError })
        return
      }
      case 'log':
        this.items.push({
          ...base,
          kind: 'note',
          text: eventSummary(event),
          isError: p.stream === 'stderr',
        })
        return
    }
  }

  /** Calls without a result when a session ends will not get one. */
  private interruptOpenCalls() {
    for (const call of this.calls.values()) {
      if (!call.result) call.interrupted = true
    }
    this.calls.clear()
  }

  /** Top level, or inside the Task call a subagent's event names. */
  private push(p: Payload, item: ChatItem) {
    const parent = str(p.parent_tool_use_id)
    const call = parent ? this.calls.get(parent) : undefined
    ;(call ? call.children : this.items).push(item)
  }
}

/** "4:02" or "1:05:09". */
export function duration(ms: number) {
  const total = Math.max(0, Math.round(ms / 1000))
  const [h, m, s] = [Math.floor(total / 3600), Math.floor((total % 3600) / 60), total % 60]
  const two = (n: number) => String(n).padStart(2, '0')
  return h > 0 ? `${h}:${two(m)}:${two(s)}` : `${m}:${two(s)}`
}

/** Tikai's own MCP tools arrive as mcp__tikai__<name>. */
export const tikaiTool = (name: string) =>
  name.startsWith('mcp__tikai__') ? name.slice('mcp__tikai__'.length) : null

/** A path under the agent's working directory without that directory. */
export function relativePath(path: string, cwd: string) {
  if (cwd && path.startsWith(cwd.endsWith('/') ? cwd : `${cwd}/`)) {
    return path.slice(cwd.length).replace(/^\/+/, '')
  }
  return path
}

const tikaiLabels: Record<string, string> = {
  get_ticket: 'Read the ticket',
  report_progress: 'Progress',
  propose_plan: 'Proposed a plan',
  ask_human: 'Asked a person',
  submit_for_review: 'Submitted for review',
  get_review_comments: 'Read the review comments',
  reply_to_thread: 'Replied to a review thread',
  get_preview_spec: 'Read the preview file format',
  check_preview_definition: 'Checked the preview file',
}

/** The tool's name as shown, and what it was called on, in one line. */
export function toolTitle(
  item: Pick<ToolItem, 'name' | 'input'>,
  cwd = '',
): {
  label: string
  detail: string
} {
  const i = item.input
  const path = (key = 'file_path') => relativePath(str(i[key]), cwd)
  const tikai = tikaiTool(item.name)
  if (tikai !== null) {
    const detail =
      tikai === 'report_progress'
        ? str(i.step)
        : tikai === 'propose_plan' || tikai === 'submit_for_review'
          ? str(i.summary)
          : tikai === 'ask_human'
            ? str(i.question)
            : ''
    return { label: tikaiLabels[tikai] ?? tikai.replaceAll('_', ' '), detail }
  }
  switch (item.name) {
    case 'Bash':
      return { label: 'Bash', detail: str(i.description) || str(i.command) }
    case 'Read': {
      const offset = num(i.offset)
      const limit = num(i.limit)
      const range =
        offset != null || limit != null
          ? ` (lines ${offset ?? 1}${limit != null ? `–${(offset ?? 1) + limit - 1}` : '+'})`
          : ''
      return { label: 'Read', detail: path() + range }
    }
    case 'Write':
    case 'Edit':
    case 'MultiEdit':
      return { label: item.name, detail: path() }
    case 'NotebookEdit':
      return { label: item.name, detail: path('notebook_path') }
    case 'Grep':
      return {
        label: 'Grep',
        detail: `${str(i.pattern)}${i.path ? ` in ${relativePath(str(i.path), cwd)}` : ''}${i.glob ? ` (${str(i.glob)})` : ''}`,
      }
    case 'Glob':
      return { label: 'Glob', detail: str(i.pattern) }
    case 'WebFetch':
      return { label: 'Fetch', detail: str(i.url) }
    case 'WebSearch':
      return { label: 'Search', detail: str(i.query) }
    case 'TodoWrite': {
      const todos = Array.isArray(i.todos) ? (i.todos as Payload[]) : []
      const done = todos.filter((t) => t.status === 'completed').length
      return { label: 'Todos', detail: `${done}/${todos.length} done` }
    }
    case 'Task':
    case 'Agent':
      return { label: 'Subagent', detail: str(i.description) || str(i.subagent_type) }
    default: {
      const json = JSON.stringify(i)
      return { label: item.name, detail: json === '{}' ? '' : json }
    }
  }
}

export type DiffLine = { op: ' ' | '-' | '+'; text: string }

/** Above this many cells the diff is not aligned: all removed lines, then all added ones. */
const maxDiffCells = 250_000

/** A line diff of two texts (longest common subsequence), for Edit cards. */
export function lineDiff(before: string, after: string): DiffLine[] {
  const a = before.split('\n')
  const b = after.split('\n')
  let start = 0
  while (start < a.length && start < b.length && a[start] === b[start]) start++
  let endA = a.length
  let endB = b.length
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) {
    endA--
    endB--
  }
  const head = a.slice(0, start).map((text): DiffLine => ({ op: ' ', text }))
  const tail = a.slice(endA).map((text): DiffLine => ({ op: ' ', text }))
  const x = a.slice(start, endA)
  const y = b.slice(start, endB)
  let middle: DiffLine[]
  if (x.length * y.length > maxDiffCells) {
    middle = [
      ...x.map((text): DiffLine => ({ op: '-', text })),
      ...y.map((text): DiffLine => ({ op: '+', text })),
    ]
  } else {
    // lcs[i][j]: length of the common subsequence of x[i..] and y[j..].
    const lcs = Array.from({ length: x.length + 1 }, () => new Uint32Array(y.length + 1))
    for (let i = x.length - 1; i >= 0; i--) {
      for (let j = y.length - 1; j >= 0; j--) {
        lcs[i][j] = x[i] === y[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1])
      }
    }
    middle = []
    let i = 0
    let j = 0
    while (i < x.length || j < y.length) {
      if (i < x.length && j < y.length && x[i] === y[j]) {
        middle.push({ op: ' ', text: x[i] })
        i++
        j++
      } else if (j < y.length && (i === x.length || lcs[i][j + 1] >= lcs[i + 1][j])) {
        middle.push({ op: '+', text: y[j++] })
      } else {
        middle.push({ op: '-', text: x[i++] })
      }
    }
    // Removed lines first within a change, as diffs usually show them.
    middle = groupChanges(middle)
  }
  return [...head, ...middle, ...tail]
}

function groupChanges(lines: DiffLine[]): DiffLine[] {
  const out: DiffLine[] = []
  let removed: DiffLine[] = []
  let added: DiffLine[] = []
  const flush = () => {
    out.push(...removed, ...added)
    removed = []
    added = []
  }
  for (const line of lines) {
    if (line.op === '-') removed.push(line)
    else if (line.op === '+') added.push(line)
    else {
      flush()
      out.push(line)
    }
  }
  flush()
  return out
}

/** Only the changed lines and a few around them; long unchanged stretches become one gap line. */
export function diffHunks(
  lines: DiffLine[],
  context = 3,
): (DiffLine | { op: 'gap'; count: number })[] {
  const keep = lines.map(() => false)
  lines.forEach((line, i) => {
    if (line.op === ' ') return
    for (let k = Math.max(0, i - context); k <= Math.min(lines.length - 1, i + context); k++) {
      keep[k] = true
    }
  })
  const out: (DiffLine | { op: 'gap'; count: number })[] = []
  let gap = 0
  lines.forEach((line, i) => {
    if (keep[i]) {
      if (gap > 0) out.push({ op: 'gap', count: gap })
      gap = 0
      out.push(line)
    } else gap++
  })
  if (gap > 0 && out.length > 0) out.push({ op: 'gap', count: gap })
  return out
}

/** The edits of an Edit or MultiEdit call as old/new pairs. */
export function edits(input: Payload): { before: string; after: string }[] {
  if (Array.isArray(input.edits)) {
    return (input.edits as Payload[]).map((e) => ({
      before: str(e.old_string),
      after: str(e.new_string),
    }))
  }
  return [{ before: str(input.old_string), after: str(input.new_string) }]
}

/** "+3 −1" for an edit. */
export function diffStat(lines: DiffLine[]) {
  const added = lines.filter((l) => l.op === '+').length
  const removed = lines.filter((l) => l.op === '-').length
  return { added, removed }
}

/** A tool result that is JSON (Tikai's tools answer with objects) as an object, else null. */
export function jsonResult(content: string): Payload | null {
  const text = content.trim()
  if (!text.startsWith('{')) return null
  try {
    const value: unknown = JSON.parse(text)
    return value && typeof value === 'object' && !Array.isArray(value) ? (value as Payload) : null
  } catch {
    return null
  }
}
