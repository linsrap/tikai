import { useState, type FormEvent } from 'react'
import { MarkdownView } from '../tickets/MarkdownView'
import { RefinePanel } from './RefinePanel'
import {
  addCriterion,
  changeCriterion,
  moveCriterion,
  priorityLabel,
  removeCriterion,
  statusLabel,
  type TicketForm,
  type TicketPriority,
  type TicketStatus,
} from '../tickets/tickets'

/** The form for a new ticket and for editing one; status only exists once the ticket does. */
export function TicketFormView({
  slug,
  initial,
  withStatus,
  submitLabel,
  pending,
  error,
  onSubmit,
  onCancel,
}: {
  slug: string
  initial: TicketForm
  withStatus: boolean
  submitLabel: string
  pending: boolean
  error: Error | null
  onSubmit: (form: TicketForm) => void
  onCancel: () => void
}) {
  const [form, setForm] = useState(initial)
  const [preview, setPreview] = useState(false)

  function submit(event: FormEvent) {
    event.preventDefault()
    onSubmit(form)
  }

  return (
    <form onSubmit={submit} className="stack">
      <label className="field">
        <span>Title</span>
        <input
          required
          maxLength={200}
          value={form.title}
          onChange={(e) => setForm({ ...form, title: e.target.value })}
        />
      </label>
      <div className="row">
        {withStatus && (
          <label className="field">
            <span>Status</span>
            <select
              value={form.status}
              onChange={(e) => setForm({ ...form, status: e.target.value as TicketStatus })}
            >
              {Object.entries(statusLabel).map(([value, label]) => (
                <option key={value} value={value}>
                  {label}
                </option>
              ))}
            </select>
          </label>
        )}
        <label className="field">
          <span>Priority</span>
          <select
            value={form.priority}
            onChange={(e) => setForm({ ...form, priority: e.target.value as TicketPriority })}
          >
            {Object.entries(priorityLabel).map(([value, label]) => (
              <option key={value} value={value}>
                {label}
              </option>
            ))}
          </select>
        </label>
      </div>
      <div className="field">
        <div className="row spread">
          <span>Description (markdown)</span>
          <button type="button" onClick={() => setPreview(!preview)}>
            {preview ? 'Write' : 'Preview'}
          </button>
        </div>
        {preview ? (
          <div className="card">
            {form.description.trim() ? (
              <MarkdownView text={form.description} />
            ) : (
              <p className="muted">Nothing to preview.</p>
            )}
          </div>
        ) : (
          <textarea
            rows={12}
            className="mono"
            value={form.description}
            placeholder="What should be done, and why? Constraints, non-goals, links."
            onChange={(e) => setForm({ ...form, description: e.target.value })}
          />
        )}
      </div>
      <fieldset className="stack">
        <legend>Acceptance criteria</legend>
        {form.criteria.length === 0 && (
          <p className="muted">No criteria yet. Each one should be checkable in a review.</p>
        )}
        {form.criteria.map((c, i) => (
          <div key={c.key} className="row criterion">
            <span className="muted">{i + 1}.</span>
            <input
              aria-label={`Criterion ${i + 1}`}
              maxLength={1000}
              value={c.text}
              onChange={(e) =>
                setForm({ ...form, criteria: changeCriterion(form.criteria, i, e.target.value) })
              }
            />
            <button
              type="button"
              aria-label={`Move criterion ${i + 1} up`}
              disabled={i === 0}
              onClick={() => setForm({ ...form, criteria: moveCriterion(form.criteria, i, -1) })}
            >
              ↑
            </button>
            <button
              type="button"
              aria-label={`Move criterion ${i + 1} down`}
              disabled={i === form.criteria.length - 1}
              onClick={() => setForm({ ...form, criteria: moveCriterion(form.criteria, i, 1) })}
            >
              ↓
            </button>
            <button
              type="button"
              aria-label={`Remove criterion ${i + 1}`}
              className="danger"
              onClick={() => setForm({ ...form, criteria: removeCriterion(form.criteria, i) })}
            >
              ✕
            </button>
          </div>
        ))}
        <p>
          <button
            type="button"
            onClick={() => setForm({ ...form, criteria: addCriterion(form.criteria) })}
          >
            Add criterion
          </button>
        </p>
      </fieldset>
      <RefinePanel slug={slug} form={form} onChange={setForm} />
      {error && (
        <p role="alert" className="error">
          {error.message}
        </p>
      )}
      <div className="row">
        <button type="submit" disabled={pending}>
          {pending ? 'Saving…' : submitLabel}
        </button>
        <button type="button" onClick={onCancel}>
          Cancel
        </button>
      </div>
    </form>
  )
}
