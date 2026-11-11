import { useState, type FormEvent } from 'react'
import { MarkdownView } from '../tickets/MarkdownView'
import {
  criterionSuggestion,
  dropCriterion,
  dropDescription,
  dropLine,
  dropTitle,
  linesInDescription,
  titleSuggestion,
  type Refined,
} from '../tickets/refine'
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
import { Button } from '../ui/Button'
import { ErrorText, Field, Hint, Input, Select, Textarea } from '../ui/form'
import { ChevronDownIcon, PlusIcon, XIcon } from '../ui/icons'
import { Segmented } from '../ui/Segmented'
import { RefinePanel, Suggestion } from './RefinePanel'

const iconButton =
  'flex size-7 shrink-0 cursor-pointer items-center justify-center rounded-md text-fg-subtle hover:bg-hover hover:text-fg disabled:cursor-default disabled:opacity-30 disabled:hover:bg-transparent'

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
  // A refine proposal written into the form: its changes are marked until dropped, edited or undone.
  const [refined, setRefined] = useState<Refined | null>(null)
  const apply = (next: { form: TicketForm; refined: Refined | null }) => {
    setForm(next.form)
    setRefined(next.refined)
  }
  const lines = linesInDescription(form, refined)

  function submit(event: FormEvent) {
    event.preventDefault()
    onSubmit(form)
  }

  return (
    <form onSubmit={submit} className="flex max-w-3xl flex-col gap-6">
      <RefinePanel
        slug={slug}
        form={form}
        refined={refined}
        onRefine={(next, nextRefined) => apply({ form: next, refined: nextRefined })}
      />

      <div className="flex flex-col gap-4 rounded-lg border border-line bg-surface p-4">
        <Field label="Title">
          <Input
            required
            maxLength={200}
            className="h-9 text-[15px] font-medium"
            value={form.title}
            onChange={(e) => setForm({ ...form, title: e.target.value })}
          />
          {refined && titleSuggestion(form, refined) && (
            <Suggestion
              was={refined.before.title}
              label="Put the old title back"
              onDrop={() => apply(dropTitle(form, refined))}
            />
          )}
        </Field>
        <div className="flex flex-wrap gap-4">
          {withStatus && (
            <Field label="Status">
              <Select
                value={form.status}
                onChange={(e) => setForm({ ...form, status: e.target.value as TicketStatus })}
              >
                {Object.entries(statusLabel).map(([value, label]) => (
                  <option key={value} value={value}>
                    {label}
                  </option>
                ))}
              </Select>
            </Field>
          )}
          <Field label="Priority">
            <Select
              value={form.priority}
              onChange={(e) => setForm({ ...form, priority: e.target.value as TicketPriority })}
            >
              {Object.entries(priorityLabel).map(([value, label]) => (
                <option key={value} value={value}>
                  {label}
                </option>
              ))}
            </Select>
          </Field>
        </div>
        <div className="flex flex-col gap-1.5">
          <div className="flex items-center justify-between gap-2">
            <span className="text-sm font-medium text-fg-2">Description (markdown)</span>
            <Segmented
              label="Description view"
              size="sm"
              value={preview ? 'preview' : 'write'}
              onChange={(v) => setPreview(v === 'preview')}
              options={[
                { value: 'write', label: 'Write' },
                { value: 'preview', label: 'Preview' },
              ]}
            />
          </div>
          {preview ? (
            <div className="min-h-[18rem] rounded-md border border-line bg-bg px-4 py-3">
              {form.description.trim() ? (
                <MarkdownView text={form.description} />
              ) : (
                <Hint>Nothing to preview.</Hint>
              )}
            </div>
          ) : (
            <Textarea
              rows={12}
              mono
              aria-label="Description (markdown)"
              value={form.description}
              placeholder="What should be done, and why? Constraints, non-goals, links."
              onChange={(e) => {
                setForm({ ...form, description: e.target.value })
                // Edited by hand: the description is the person's now, so "put back" would lose their edit.
                if (refined?.description) setRefined({ ...refined, description: false })
              }}
            />
          )}
          {refined?.description && (
            <Suggestion
              label="Put the old description back"
              onDrop={() => apply(dropDescription(form, refined))}
            >
              Rewrote the description.
            </Suggestion>
          )}
          {refined &&
            lines.map((line) => (
              <Suggestion
                key={line}
                label={`Remove “${line}”`}
                onDrop={() => apply(dropLine(form, refined, line))}
              >
                {line}
              </Suggestion>
            ))}
        </div>
      </div>

      <fieldset className="flex flex-col gap-2">
        <legend className="mb-2 p-0 text-sm font-semibold">Acceptance criteria</legend>
        {form.criteria.length === 0 && (
          <Hint>No criteria yet. Each one should be checkable in a review.</Hint>
        )}
        {form.criteria.length > 0 && (
          <ol className="m-0 flex list-none flex-col gap-1.5 p-0">
            {form.criteria.map((c, i) => {
              const suggestion = criterionSuggestion(refined, c)
              return (
                <li key={c.key} className="flex flex-col gap-1">
                  <div className="flex items-center gap-1.5">
                    <span className="w-6 shrink-0 text-right text-sm text-fg-subtle tabular-nums">
                      {i + 1}.
                    </span>
                    <Input
                      aria-label={`Criterion ${i + 1}`}
                      maxLength={1000}
                      className="min-w-0 flex-1"
                      value={c.text}
                      onChange={(e) =>
                        setForm({
                          ...form,
                          criteria: changeCriterion(form.criteria, i, e.target.value),
                        })
                      }
                    />
                    <button
                      type="button"
                      className={iconButton}
                      aria-label={`Move criterion ${i + 1} up`}
                      disabled={i === 0}
                      onClick={() =>
                        setForm({ ...form, criteria: moveCriterion(form.criteria, i, -1) })
                      }
                    >
                      <ChevronDownIcon size={14} className="rotate-180" />
                    </button>
                    <button
                      type="button"
                      className={iconButton}
                      aria-label={`Move criterion ${i + 1} down`}
                      disabled={i === form.criteria.length - 1}
                      onClick={() =>
                        setForm({ ...form, criteria: moveCriterion(form.criteria, i, 1) })
                      }
                    >
                      <ChevronDownIcon size={14} />
                    </button>
                    <button
                      type="button"
                      className={`${iconButton} hover:bg-bad-bg hover:text-bad`}
                      aria-label={`Remove criterion ${i + 1}`}
                      onClick={() =>
                        setForm({ ...form, criteria: removeCriterion(form.criteria, i) })
                      }
                    >
                      <XIcon size={14} />
                    </button>
                  </div>
                  {refined && suggestion && (
                    <div className="ml-[30px] pr-[90px]">
                      <Suggestion
                        was={suggestion.was ?? undefined}
                        label={
                          suggestion.was != null
                            ? `Put criterion ${i + 1} back`
                            : `Remove suggested criterion ${i + 1}`
                        }
                        onDrop={() => apply(dropCriterion(form, refined, c.key))}
                      >
                        {suggestion.was == null && 'New criterion.'}
                      </Suggestion>
                    </div>
                  )}
                </li>
              )
            })}
          </ol>
        )}
        <div>
          <Button
            variant="ghost"
            size="sm"
            className="-ml-2"
            onClick={() => setForm({ ...form, criteria: addCriterion(form.criteria) })}
          >
            <PlusIcon size={14} />
            Add criterion
          </Button>
        </div>
      </fieldset>

      {error && <ErrorText>{error.message}</ErrorText>}
      <div className="flex flex-wrap gap-2 border-t border-line pt-4">
        <Button type="submit" variant="primary" disabled={pending}>
          {pending ? 'Saving…' : submitLabel}
        </Button>
        <Button variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </form>
  )
}
