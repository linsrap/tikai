import { useState } from 'react'
import { MarkdownView } from '../tickets/MarkdownView'
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
      className="stack comment-form"
      onSubmit={(e) => {
        e.preventDefault()
        if (!empty) onSave(body, initialAction ?? 'note')
      }}
    >
      <label className="field">
        <span className="muted small">{label}</span>
        <textarea
          rows={3}
          value={body}
          onChange={(e) => setBody(e.target.value)}
          autoFocus
          maxLength={10000}
        />
      </label>
      <div className="row">
        <button
          type="button"
          className={initialAction === 'send_to_agent' || !initialAction ? 'primary' : undefined}
          disabled={pending || empty}
          onClick={() => onSave(body, 'send_to_agent')}
          title="Sent to the agent with your next request for changes"
        >
          Send to agent
        </button>
        <button
          type="button"
          disabled={pending || empty}
          onClick={() => onSave(body, 'note')}
          title="For people only"
        >
          Add as note
        </button>
        {onCancel && (
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
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
    <div className="card thread">
      <CommentForm
        label={label}
        pending={threads.create.isPending}
        onSave={(body, action) =>
          threads.create.mutate({ ...anchor, body, action }, { onSuccess: onDone })
        }
        onCancel={onDone}
      />
      {threads.create.error && <p className="error">{threads.create.error.message}</p>}
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
    <li className={`comment ${comment.author}`} data-testid="comment">
      <p className="row small">
        <strong>{comment.authorName}</strong>
        <span className="muted">{new Date(comment.createdAt).toLocaleString()}</span>
        {comment.author === 'person' &&
          (comment.action === 'note' ? (
            <span className="badge">note</span>
          ) : comment.sentAt ? (
            <span className="badge sent">sent to agent</span>
          ) : (
            <span className="badge unsent">for the agent, not sent yet</span>
          ))}
        {comment.editedAt && <span className="muted">edited</span>}
        {comment.canEdit && !locked && (
          <>
            <button type="button" className="link" onClick={() => setEditing(true)}>
              Edit
            </button>
            <button
              type="button"
              className="link"
              disabled={threads.remove.isPending}
              onClick={() => {
                if (window.confirm('Delete this comment?')) threads.remove.mutate(comment.id)
              }}
            >
              Delete
            </button>
          </>
        )}
      </p>
      <MarkdownView text={comment.body} />
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
    <div className="stack">
      <ul className="comments">
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
          <div className="row">
            <button type="button" onClick={() => setReplying(true)}>
              Reply
            </button>
            <button
              type="button"
              disabled={threads.status.isPending}
              onClick={() => threads.status.mutate({ threadId: thread.id, resolve: !resolved })}
            >
              {resolved ? 'Reopen' : 'Resolve'}
            </button>
          </div>
        ))}
      {[threads.reply, threads.status, threads.edit, threads.remove].map(
        (m, i) =>
          m.error && (
            <p key={i} className="error">
              {m.error.message}
            </p>
          ),
      )}
    </div>
  )
  return (
    <details
      className={`card thread ${thread.status}`}
      data-testid="thread"
      data-status={thread.status}
      open={open}
      onToggle={(e) => setOpen(e.currentTarget.open)}
    >
      <summary className="small">
        {showPlace ? <span className="mono">{threadPlace(thread)}</span> : <span>Thread</span>}{' '}
        <span className="muted">
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
    <section className="stack" aria-label="General comments">
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
            <button type="button" onClick={() => setWriting(true)}>
              Comment on the whole change
            </button>
          </div>
        ))}
    </section>
  )
}
