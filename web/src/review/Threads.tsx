import { useState } from 'react'
import { MarkdownView } from '../tickets/MarkdownView'
import { Button } from '../ui/Button'
import { cx } from '../ui/cx'
import { ErrorText, Textarea } from '../ui/form'
import { ChevronRightIcon } from '../ui/icons'
import { ConfirmDialog } from '../ui/overlay'
import { Tag } from '../ui/Tag'
import {
  threadPlace,
  useThreads,
  type CommentAction,
  type NewThread,
  type ReviewComment,
  type ReviewThread,
} from './review'

type Threads = ReturnType<typeof useThreads>

/**
 * A comment box with two ways to save: as a note for people, or for the agent (sent with the next request for
 * changes).
 */
export function CommentForm({
  label,
  pending,
  onSave,
  onCancel,
  initial = '',
  initialAction,
}: {
  label: string
  pending: boolean
  onSave: (body: string, action: CommentAction) => void
  onCancel?: () => void
  initial?: string
  /** When editing: the action the comment has; both buttons save, the pressed one sets the action. */
  initialAction?: CommentAction
}) {
  const [body, setBody] = useState(initial)
  const empty = body.trim() === ''
  return (
    <form
      className="flex flex-col gap-2"
      onSubmit={(e) => {
        e.preventDefault()
        if (!empty) onSave(body, initialAction ?? 'note')
      }}
    >
      <label className="flex flex-col gap-1.5">
        <span className="text-xs font-medium text-fg-muted">{label}</span>
        <Textarea
          rows={3}
          className="min-h-16"
          value={body}
          onChange={(e) => setBody(e.target.value)}
          autoFocus
          maxLength={10000}
        />
      </label>
      <div className="flex flex-wrap items-center gap-2">
        <Button
          size="sm"
          variant={initialAction === 'note' ? 'secondary' : 'primary'}
          disabled={pending || empty}
          onClick={() => onSave(body, 'send_to_agent')}
          title="Sent to the agent with your next request for changes"
        >
          Send to agent
        </Button>
        <Button
          size="sm"
          variant={initialAction === 'note' ? 'primary' : 'secondary'}
          disabled={pending || empty}
          onClick={() => onSave(body, 'note')}
          title="For people only"
        >
          Add as note
        </Button>
        {onCancel && (
          <Button size="sm" variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
        )}
      </div>
    </form>
  )
}

/** A new thread on lines of a pane (or a general one), saved through the threads API. */
export function NewThreadForm({
  threads,
  anchor,
  label,
  onDone,
}: {
  threads: Threads
  anchor: Omit<NewThread, 'body' | 'action'>
  label: string
  onDone: () => void
}) {
  return (
    <div className="flex flex-col gap-2 rounded-lg border border-line bg-surface p-3 shadow-[0_1px_2px_rgb(0_0_0/0.04)]">
      <CommentForm
        label={label}
        pending={threads.create.isPending}
        onSave={(body, action) =>
          threads.create.mutate({ ...anchor, body, action }, { onSuccess: onDone })
        }
        onCancel={onDone}
      />
      {threads.create.error && <ErrorText>{threads.create.error.message}</ErrorText>}
    </div>
  )
}

function CommentView({
  comment,
  threads,
  locked,
}: {
  comment: ReviewComment
  threads: Threads
  locked: boolean
}) {
  const [editing, setEditing] = useState(false)
  if (editing) {
    return (
      <CommentForm
        label="Change your comment"
        initial={comment.body}
        initialAction={comment.action}
        pending={threads.edit.isPending}
        onSave={(body, action) =>
          threads.edit.mutate(
            { commentId: comment.id, body, action },
            { onSuccess: () => setEditing(false) },
          )
        }
        onCancel={() => setEditing(false)}
      />
    )
  }
  return (
    <li className="flex gap-2.5" data-testid="comment">
      <span
        aria-hidden="true"
        className={cx(
          'mt-px flex size-[22px] shrink-0 items-center justify-center rounded-full text-[10px] font-semibold',
          comment.author === 'agent' ? 'bg-run-bg text-run' : 'bg-line-strong text-fg-2',
        )}
      >
        {initials(comment.authorName)}
      </span>
      <div className="min-w-0 flex-1">
        <p className="m-0 mb-0.5 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs">
          <strong className="text-sm font-semibold">{comment.authorName}</strong>
          <span className="text-fg-subtle">{new Date(comment.createdAt).toLocaleString()}</span>
          {comment.author === 'person' &&
            (comment.action === 'note' ? (
              <Tag className="h-[18px] px-1.5">note</Tag>
            ) : comment.sentAt ? (
              <Tag tone="ok" className="h-[18px] px-1.5">
                sent to agent
              </Tag>
            ) : (
              <Tag tone="wait" className="h-[18px] px-1.5">
                for the agent, not sent yet
              </Tag>
            ))}
          {comment.editedAt && <span className="text-fg-subtle">edited</span>}
          {comment.canEdit && !locked && (
            <span className="ml-auto flex gap-1">
              <button
                type="button"
                className="cursor-pointer rounded px-1 text-fg-muted hover:bg-hover hover:text-fg"
                onClick={() => setEditing(true)}
              >
                Edit
              </button>
              <ConfirmDialog
                trigger={
                  <button
                    type="button"
                    className="cursor-pointer rounded px-1 text-fg-muted hover:bg-bad-bg hover:text-bad disabled:opacity-50"
                    disabled={threads.remove.isPending}
                  >
                    Delete
                  </button>
                }
                title="Delete this comment?"
                confirmLabel="Delete comment"
                danger
                onConfirm={() => threads.remove.mutate(comment.id)}
              />
            </span>
          )}
        </p>
        <div className="text-sm">
          <MarkdownView text={comment.body} />
        </div>
      </div>
    </li>
  )
}

/** A thread with its comments, a reply box and resolve; resolved threads start collapsed. */
export function ThreadView({
  thread,
  threads,
  locked,
  showPlace = false,
}: {
  thread: ReviewThread
  threads: Threads
  /** The change is closed: nothing can be added or changed. */
  locked: boolean
  showPlace?: boolean
}) {
  const [replying, setReplying] = useState(false)
  const [open, setOpen] = useState(thread.status === 'open')
  const resolved = thread.status === 'resolved'
  const body = (
    <div className="flex flex-col gap-3 px-3 pt-1 pb-3">
      <ul className="m-0 flex list-none flex-col gap-3 p-0">
        {thread.comments.map((c) => (
          <CommentView key={c.id} comment={c} threads={threads} locked={locked} />
        ))}
      </ul>
      {!locked &&
        (replying ? (
          <CommentForm
            label="Reply"
            pending={threads.reply.isPending}
            onSave={(text, action) =>
              threads.reply.mutate(
                { threadId: thread.id, body: text, action },
                { onSuccess: () => setReplying(false) },
              )
            }
            onCancel={() => setReplying(false)}
          />
        ) : (
          <div className="flex flex-wrap gap-2 pl-8">
            <Button size="sm" onClick={() => setReplying(true)}>
              Reply
            </Button>
            <Button
              size="sm"
              variant="ghost"
              disabled={threads.status.isPending}
              onClick={() => threads.status.mutate({ threadId: thread.id, resolve: !resolved })}
            >
              {resolved ? 'Reopen' : 'Resolve'}
            </Button>
          </div>
        ))}
      {[threads.reply, threads.status, threads.edit, threads.remove].map(
        (m, i) => m.error && <ErrorText key={i}>{m.error.message}</ErrorText>,
      )}
    </div>
  )
  return (
    <details
      className="group/thread rounded-lg border border-line bg-surface shadow-[0_1px_2px_rgb(0_0_0/0.04)]"
      data-testid="thread"
      data-status={thread.status}
      open={open}
      onToggle={(e) => setOpen(e.currentTarget.open)}
    >
      <summary className="flex cursor-pointer list-none items-center gap-1.5 px-3 py-2 text-xs text-fg-muted hover:text-fg [&::-webkit-details-marker]:hidden">
        <ChevronRightIcon
          size={13}
          className="shrink-0 transition-transform group-open/thread:rotate-90"
        />
        {showPlace ? (
          <span className="font-mono text-fg-2">{threadPlace(thread)}</span>
        ) : (
          <span className="font-medium text-fg-2">Thread</span>
        )}
        <span>
          · {thread.comments.length} comment{thread.comments.length === 1 ? '' : 's'}
          {resolved && ` · resolved${thread.resolvedBy ? ` by ${thread.resolvedBy}` : ''}`}
        </span>
      </summary>
      {body}
    </details>
  )
}

/** Comments on the round as a whole, and a box for a new one. */
export function GeneralComments({
  roundNo,
  list,
  threads,
  locked,
}: {
  roundNo: number
  list: readonly ReviewThread[]
  threads: Threads
  locked: boolean
}) {
  const [writing, setWriting] = useState(false)
  return (
    <section className="flex flex-col gap-2.5" aria-label="General comments">
      {list.map((t) => (
        <ThreadView key={t.id} thread={t} threads={threads} locked={locked} showPlace />
      ))}
      {!locked &&
        (writing ? (
          <NewThreadForm
            threads={threads}
            anchor={{ roundNo }}
            label={`A comment on round ${roundNo} as a whole`}
            onDone={() => setWriting(false)}
          />
        ) : (
          <div>
            <Button size="sm" onClick={() => setWriting(true)}>
              Comment on the whole change
            </Button>
          </div>
        ))}
    </section>
  )
}

function initials(name: string) {
  return name
    .split(/\s+/)
    .map((part) => part[0])
    .join('')
    .slice(0, 2)
    .toUpperCase()
}
