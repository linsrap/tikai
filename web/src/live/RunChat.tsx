import { memo, useState, type ReactNode } from 'react'
import { MarkdownView } from '../tickets/MarkdownView'
import { cx } from '../ui/cx'
import { CheckIcon, ChevronDownIcon, ChevronRightIcon } from '../ui/icons'
import { Dot, type Tone } from '../ui/Tag'
import {
  diffHunks,
  diffStat,
  edits,
  jsonResult,
  lineDiff,
  tikaiTool,
  toolTitle,
  type ChatItem,
  type ToolItem,
} from './chat'

type Payload = Record<string, unknown>

const str = (value: unknown) => (typeof value === 'string' ? value : '')
const list = (value: unknown) => (Array.isArray(value) ? value.map(str).filter(Boolean) : [])

/**
 * Which cards a person opened or closed, per log: rows leave the DOM when they scroll out of view, and come back
 * as they were left.
 */
export type OpenState = Map<string, boolean>

/** One row of the chat view. */
export function ChatRow({ item, cwd, opened }: { item: ChatItem; cwd: string; opened: OpenState }) {
  switch (item.kind) {
    case 'text':
      return item.role === 'assistant' ? (
        <div className="px-3 py-1.5">
          <AssistantText text={item.text} />
        </div>
      ) : (
        <div className="mx-3 my-1.5 rounded-md bg-sunken px-3 py-2">
          <p className="m-0 mb-1 text-xs text-fg-subtle">Prompt</p>
          <AssistantText text={item.text} />
        </div>
      )
    case 'thinking':
      return (
        <Fold
          id={item.key}
          opened={opened}
          className="mx-3 my-0.5"
          header={
            <span className="flex min-w-0 gap-2 text-xs text-fg-subtle">
              <span className="shrink-0">Thinking</span>
              <span className="truncate text-fg-faint">{firstLine(item.text)}</span>
            </span>
          }
        >
          <p className="m-0 py-1 pl-5 text-xs leading-5 whitespace-pre-wrap text-fg-muted">
            {item.text}
          </p>
        </Fold>
      )
    case 'tool':
      return <ToolCard item={item} cwd={cwd} opened={opened} />
    case 'session':
    case 'end':
      return (
        <div
          className={cx(
            'flex items-center gap-3 px-3 py-2 text-xs',
            (item.kind === 'session' ? item.problem : item.isError) ? 'text-bad' : 'text-fg-subtle',
          )}
        >
          <span className="h-px flex-1 bg-line" />
          <span className="min-w-0 text-center">{item.text}</span>
          <span className="h-px flex-1 bg-line" />
        </div>
      )
    case 'note':
      return (
        <p
          className={cx(
            'm-0 truncate px-3 py-0.5 font-mono text-xs',
            item.isError ? 'text-bad' : 'text-fg-subtle',
          )}
          title={item.text}
        >
          {item.text}
        </p>
      )
  }
}

/** Markdown is parsed once per text, not on every new event of a live run. */
const AssistantText = memo(function AssistantText({ text }: { text: string }) {
  return <MarkdownView text={text} />
})

const firstLine = (text: string) => text.trim().split('\n', 1)[0]

/** A header that opens a body; remembers its state in `opened` across scrolling. */
function Fold({
  id,
  opened,
  defaultOpen = false,
  header,
  className,
  children,
}: {
  id: string
  opened: OpenState
  defaultOpen?: boolean
  header: ReactNode
  className?: string
  children: ReactNode
}) {
  const [open, setOpen] = useState(() => opened.get(id) ?? defaultOpen)
  const Chevron = open ? ChevronDownIcon : ChevronRightIcon
  return (
    <div className={className}>
      <button
        type="button"
        aria-expanded={open}
        onClick={() => {
          opened.set(id, !open)
          setOpen(!open)
        }}
        className="flex w-full min-w-0 cursor-pointer items-center gap-1.5 rounded text-left hover:bg-hover"
      >
        <Chevron size={14} className="shrink-0 text-fg-faint" />
        {header}
      </button>
      {open && children}
    </div>
  )
}

/** Blue while it runs, amber while a person is asked, red on an error. */
function toolTone(item: ToolItem): Tone | null {
  if (item.result) return item.result.isError ? 'bad' : null
  if (item.interrupted) return 'neutral'
  const tikai = tikaiTool(item.name)
  return tikai === 'ask_human' || tikai === 'propose_plan' ? 'wait' : 'run'
}

function toolState(item: ToolItem) {
  if (item.result) return item.result.isError ? 'failed' : null
  if (item.interrupted) return 'no result'
  const tikai = tikaiTool(item.name)
  return tikai === 'ask_human' || tikai === 'propose_plan' ? 'waiting for a person' : 'running'
}

function ToolCard({ item, cwd, opened }: { item: ToolItem; cwd: string; opened: OpenState }) {
  const { label, detail } = toolTitle(item, cwd)
  const tone = toolTone(item)
  const state = toolState(item)
  const body = toolBody(item, cwd, opened)
  const header = (
    <span className="flex min-w-0 flex-1 items-center gap-2 py-1 pr-2">
      {tone && <Dot tone={tone} />}
      <span className="shrink-0 font-medium text-fg">{label}</span>
      <span className="min-w-0 truncate font-mono text-xs text-fg-muted">{detail}</span>
      <span className="ml-auto flex shrink-0 items-center gap-2 text-xs text-fg-subtle">
        {body.meta}
        {state && <span className={cx(tone === 'bad' && 'text-bad')}>{state}</span>}
      </span>
    </span>
  )
  return (
    <article
      aria-label={detail ? `${label}: ${detail}` : label}
      className="mx-3 my-1 overflow-hidden rounded-md border border-line bg-surface"
    >
      {body.content ? (
        <Fold
          id={item.key}
          opened={opened}
          defaultOpen={body.open || item.result?.isError === true}
          header={header}
          className="[&>button]:rounded-none [&>button]:px-2"
        >
          <div className="border-t border-line">
            {body.content}
            {(item.truncated || item.result?.truncated) && (
              <p className="m-0 border-t border-line-soft px-3 py-1 text-xs text-fg-subtle">
                Cut to fit the log; the agent saw all of it.
              </p>
            )}
          </div>
        </Fold>
      ) : (
        <div className="flex px-2 pl-7">{header}</div>
      )}
    </article>
  )
}

function Pre({ children, error = false }: { children: ReactNode; error?: boolean }) {
  return (
    <pre
      className={cx(
        'm-0 max-h-80 overflow-auto px-3 py-2 font-mono text-xs leading-5 [overflow-wrap:anywhere] whitespace-pre-wrap',
        error ? 'text-bad' : 'text-fg-2',
      )}
    >
      {children}
    </pre>
  )
}

/** The tool's result as text, or nothing while it runs or when it is empty. */
function ResultText({ item, divider = false }: { item: ToolItem; divider?: boolean }) {
  if (!item.result || !item.result.content.trim()) return null
  return (
    <div className={cx(divider && 'border-t border-line-soft')}>
      <Pre error={item.result.isError}>{item.result.content}</Pre>
    </div>
  )
}

type Body = { content: ReactNode; open?: boolean; meta?: ReactNode }

/** What opens under the card's header, per tool; anything unknown shows its input and result. */
function toolBody(item: ToolItem, cwd: string, opened: OpenState): Body {
  const input = item.input
  const tikai = tikaiTool(item.name)
  if (tikai !== null) return tikaiBody(tikai, item)
  switch (item.name) {
    case 'Bash':
      return {
        content: (
          <>
            <Pre>
              <span className="text-fg-subtle">$ </span>
              {str(input.command)}
            </Pre>
            <ResultText item={item} divider />
          </>
        ),
      }
    case 'Edit':
    case 'MultiEdit': {
      const diffs = edits(input).map((e) => lineDiff(e.before, e.after))
      const stat = diffs
        .map(diffStat)
        .reduce((a, b) => ({ added: a.added + b.added, removed: a.removed + b.removed }), {
          added: 0,
          removed: 0,
        })
      const lines = diffs.reduce((n, d) => n + d.length, 0)
      return {
        open: lines <= 40,
        meta: (
          <span className="font-mono tabular-nums">
            <span className="text-ok">+{stat.added}</span>{' '}
            <span className="text-bad">−{stat.removed}</span>
          </span>
        ),
        content: (
          <>
            {diffs.map((diff, i) => (
              <Diff key={i} lines={diff} />
            ))}
            {input.replace_all === true && (
              <p className="m-0 px-3 py-1 text-xs text-fg-subtle">Replaced every occurrence.</p>
            )}
            {item.result?.isError && <ResultText item={item} divider />}
          </>
        ),
      }
    }
    case 'Write': {
      const content = str(input.content)
      const lines = content ? content.split('\n').length : 0
      return {
        meta: <span className="tabular-nums">{lines} lines</span>,
        content: (
          <>
            <Pre>{content}</Pre>
            {item.result?.isError && <ResultText item={item} divider />}
          </>
        ),
      }
    }
    case 'TodoWrite': {
      const todos = Array.isArray(input.todos) ? (input.todos as Payload[]) : []
      return {
        open: true,
        content: (
          <ul className="m-0 list-none px-3 py-1.5">
            {todos.map((todo, i) => (
              <li key={i} className="flex items-start gap-2 py-0.5 text-sm">
                <TodoMark status={str(todo.status)} />
                <span
                  className={cx(
                    todo.status === 'completed' && 'text-fg-subtle line-through',
                    todo.status === 'in_progress' && 'text-fg',
                  )}
                >
                  {str(todo.content)}
                </span>
              </li>
            ))}
          </ul>
        ),
      }
    }
    case 'Task':
    case 'Agent':
      return {
        meta:
          item.children.length > 0 ? (
            <span className="tabular-nums">{item.children.length} steps</span>
          ) : undefined,
        content: (
          <>
            {str(input.prompt) && (
              <div className="border-b border-line-soft px-3 py-2">
                <p className="m-0 mb-1 text-xs text-fg-subtle">Prompt</p>
                <p className="m-0 text-sm whitespace-pre-wrap text-fg-2">{str(input.prompt)}</p>
              </div>
            )}
            {item.children.length > 0 && (
              <div className="py-1">
                {item.children.map((child) => (
                  <ChatRow key={child.key} item={child} cwd={cwd} opened={opened} />
                ))}
              </div>
            )}
            {item.result && (
              <div className="border-t border-line-soft px-3 py-2">
                <AssistantText text={item.result.content} />
              </div>
            )}
          </>
        ),
      }
    case 'Read':
    case 'Grep':
    case 'Glob':
    case 'WebFetch':
    case 'WebSearch':
      return {
        meta: item.result && !item.result.isError ? <LineCount text={item.result.content} /> : null,
        content: item.result?.content.trim() ? <ResultText item={item} /> : null,
      }
    default:
      return {
        content: (
          <>
            {Object.keys(input).length > 0 && <Pre>{JSON.stringify(input, null, 2)}</Pre>}
            <ResultText item={item} divider={Object.keys(input).length > 0} />
          </>
        ),
      }
  }
}

function tikaiBody(tool: string, item: ToolItem): Body {
  const input = item.input
  const answer = item.result ? jsonResult(item.result.content) : null
  const field = (name: string) => {
    if (!answer) return ''
    const key = Object.keys(answer).find((k) => k.toLowerCase() === name)
    return key ? str(answer[key]) : ''
  }
  switch (tool) {
    case 'report_progress':
      return { content: str(input.note) ? <Pre>{str(input.note)}</Pre> : null }
    case 'propose_plan': {
      const steps = list(input.steps)
      const risks = list(input.risks)
      const decision = field('decision')
      return {
        open: true,
        content: (
          <div className="flex flex-col gap-2 px-3 py-2 text-sm">
            {steps.length > 0 && (
              <ol className="m-0 list-decimal pl-5 text-fg-2">
                {steps.map((step, i) => (
                  <li key={i}>{step}</li>
                ))}
              </ol>
            )}
            {risks.length > 0 && (
              <p className="m-0 text-xs text-fg-muted">Risks: {risks.join('; ')}</p>
            )}
            {item.result &&
              (decision ? (
                <p className="m-0 text-xs text-fg-2">
                  <span className="text-fg-subtle">Answer: </span>
                  {decision === 'approve' ? 'Approved' : decision.replaceAll('_', ' ')}
                  {field('notes') && ` · ${field('notes')}`}
                </p>
              ) : (
                <ResultText item={item} />
              ))}
          </div>
        ),
      }
    }
    case 'ask_human': {
      const options = list(input.options)
      const recommended = str(input.recommended)
      const reply = field('answer')
      return {
        open: true,
        content: (
          <div className="flex flex-col gap-2 px-3 py-2 text-sm">
            {options.length > 0 && (
              <ul className="m-0 list-disc pl-5 text-fg-2">
                {options.map((option, i) => (
                  <li key={i}>
                    {option}
                    {option === recommended && (
                      <span className="text-xs text-fg-subtle"> (recommended)</span>
                    )}
                  </li>
                ))}
              </ul>
            )}
            {str(input.context) && (
              <p className="m-0 text-xs whitespace-pre-wrap text-fg-muted">{str(input.context)}</p>
            )}
            {item.result &&
              (reply ? (
                <p className="m-0 text-xs text-fg-2">
                  <span className="text-fg-subtle">Answer: </span>
                  {reply}
                </p>
              ) : (
                <ResultText item={item} />
              ))}
          </div>
        ),
      }
    }
    default:
      return {
        content:
          Object.keys(input).length > 0 || item.result?.content.trim() ? (
            <>
              {Object.keys(input).length > 0 && <Pre>{JSON.stringify(input, null, 2)}</Pre>}
              <ResultText item={item} divider={Object.keys(input).length > 0} />
            </>
          ) : null,
      }
  }
}

function LineCount({ text }: { text: string }) {
  const n = text.trim() ? text.trim().split('\n').length : 0
  return (
    <span className="tabular-nums">
      {n} line{n === 1 ? '' : 's'}
    </span>
  )
}

function TodoMark({ status }: { status: string }) {
  if (status === 'completed') return <CheckIcon size={14} className="mt-0.5 shrink-0 text-ok" />
  if (status === 'in_progress') return <Dot tone="run" className="mx-[3.5px] mt-1.5" />
  return (
    <span
      aria-hidden="true"
      className="mt-1 inline-block size-3 shrink-0 rounded-full border border-line-strong"
    />
  )
}

function Diff({ lines }: { lines: ReturnType<typeof lineDiff> }) {
  const hunks = diffHunks(lines)
  if (hunks.length === 0) return <p className="m-0 px-3 py-1 text-xs text-fg-subtle">No change.</p>
  return (
    <div className="max-h-96 overflow-auto py-1 font-mono text-xs leading-5">
      {hunks.map((line, i) =>
        line.op === 'gap' ? (
          <div key={i} className="px-3 text-fg-faint">
            ⋯ {line.count} unchanged line{line.count === 1 ? '' : 's'}
          </div>
        ) : (
          <div
            key={i}
            className={cx(
              'flex gap-2 px-3 [overflow-wrap:anywhere] whitespace-pre-wrap',
              line.op === '+' && 'bg-ok-bg text-ok',
              line.op === '-' && 'bg-bad-bg text-bad',
              line.op === ' ' && 'text-fg-2',
            )}
          >
            <span aria-hidden="true" className="w-2 shrink-0 select-none">
              {line.op === ' ' ? '' : line.op === '-' ? '−' : '+'}
            </span>
            <span className="min-w-0">{line.text || ' '}</span>
          </div>
        ),
      )}
    </div>
  )
}
