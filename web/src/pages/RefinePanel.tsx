import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { useState, type ReactNode } from 'react'
import { orderedOptions } from '../inbox/inbox'
import { providersQuery } from '../llm/llm'
import { decide, decision, refineForm, type Refined } from '../tickets/refine'
import {
  largerSize,
  refinedCriterionKeys,
  toRefineSpecRequest,
  useRefineSpec,
  type SpecSize,
  type TicketForm,
} from '../tickets/tickets'
import { Button } from '../ui/Button'
import { ErrorText, Hint, Select } from '../ui/form'
import { XIcon } from '../ui/icons'
import { Tag } from '../ui/Tag'

/**
 * "Refine with AI": sends the form as it is to an LLM and writes the proposal straight into the form, where every
 * change is marked and can be dropped on its own. "Undo refine" puts the form back as it was; nothing is saved until
 * the form's own Save button.
 */
export function RefinePanel({
  slug,
  form,
  refined,
  onRefine,
}: {
  slug: string
  form: TicketForm
  refined: Refined | null
  onRefine: (form: TicketForm, refined: Refined | null) => void
}) {
  const projectProviders = useQuery(providersQuery({ kind: 'project', slug }))
  const ownProviders = useQuery(providersQuery({ kind: 'user' }))
  // The server prefers the project's providers too; this list shows them first.
  const providers = [...(projectProviders.data ?? []), ...(ownProviders.data ?? [])]
  const [providerId, setProviderId] = useState<string | null>(null)
  const refine = useRefineSpec(slug)

  if (projectProviders.isPending || ownProviders.isPending) {
    return null
  }
  if (providers.length === 0) {
    return (
      <Hint>
        To refine tickets with AI, add an LLM provider to the{' '}
        <Link to="/projects/$slug" params={{ slug }}>
          project
        </Link>{' '}
        or your own under <Link to="/settings">Settings</Link>.
      </Hint>
    )
  }

  /** Refines the form as it was before any refine, so "More detail" does not refine a refinement. */
  function start(minSize?: SpecSize) {
    const base = refined?.before ?? form
    const keys = refinedCriterionKeys(base)
    refine.mutate(toRefineSpecRequest(base, providerId, minSize), {
      onSuccess: (proposal) => {
        const next = refineForm(base, proposal, keys)
        onRefine(next.form, next.refined)
      },
    })
  }

  const proposal = refined?.proposal
  const more = proposal ? largerSize(proposal.size) : null
  const unchanged =
    proposal &&
    proposal.title == null &&
    proposal.description == null &&
    proposal.criteria.length === 0 &&
    proposal.assumptions.length === 0 &&
    proposal.questions.length === 0

  return (
    <section
      className="flex flex-col gap-3 rounded-lg border border-line bg-surface px-4 py-3"
      aria-label="Refine with AI"
    >
      <div className="flex flex-wrap items-center gap-3">
        <div className="min-w-0 flex-1">
          {proposal ? (
            <p className="m-0 flex flex-wrap items-center gap-2 text-sm">
              <Tag tone="wait">{proposal.size}</Tag>
              <span className="text-fg-2">
                {unchanged
                  ? 'The ticket is fine as it is.'
                  : 'Refined: the changes are marked in the form. Drop what you do not want, then save.'}
              </span>
            </p>
          ) : (
            <>
              <h2 className="m-0 text-sm font-semibold">Refine with AI</h2>
              <Hint>Writes a sharper ticket into the form, marked so you can drop any change.</Hint>
            </>
          )}
        </div>
        {providers.length > 1 && !proposal && (
          <label className="flex items-center gap-2 text-sm text-fg-muted">
            <span>using</span>
            <Select
              value={providerId ?? ''}
              onChange={(e) => setProviderId(e.target.value || null)}
              disabled={refine.isPending}
            >
              <option value="">{providers[0].name} (default)</option>
              {providers.slice(1).map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                  {p.ownerProjectId ? '' : ' (yours)'}
                </option>
              ))}
            </Select>
          </label>
        )}
        {proposal ? (
          <div className="flex flex-wrap gap-2">
            {more && (
              <Button size="sm" disabled={refine.isPending} onClick={() => start(more)}>
                {refine.isPending ? 'Refining…' : 'More detail'}
              </Button>
            )}
            <Button
              size="sm"
              variant="ghost"
              disabled={refine.isPending}
              onClick={() => onRefine(refined.before, null)}
            >
              Undo refine
            </Button>
          </div>
        ) : (
          <Button disabled={refine.isPending || !form.title.trim()} onClick={() => start()}>
            {refine.isPending ? 'Refining… (can take a minute)' : 'Refine with AI'}
          </Button>
        )}
      </div>
      {refine.error && <ErrorText>{refine.error.message}</ErrorText>}
      {refined && proposal && proposal.questions.length > 0 && (
        <div className="flex flex-col gap-3 border-t border-line-soft pt-3">
          {proposal.questions.map((q) => {
            const chosen = decision(form, refined, q.question)
            return (
              <div
                key={q.question}
                className="flex flex-col gap-2"
                role="group"
                aria-label={q.question}
              >
                <p className="m-0">{q.question}</p>
                <div className="flex flex-wrap gap-2">
                  {orderedOptions(q).map((option) => (
                    <Button
                      key={option}
                      size="sm"
                      variant={chosen === option ? 'primary' : 'secondary'}
                      aria-pressed={chosen === option}
                      onClick={() => {
                        const next = decide(form, refined, q.question, option)
                        onRefine(next.form, next.refined)
                      }}
                    >
                      {option}
                      {option === q.recommended && (
                        <>
                          {' '}
                          <span className="text-xs font-normal text-fg-subtle">recommended</span>
                        </>
                      )}
                    </Button>
                  ))}
                </div>
              </div>
            )
          })}
          <Hint>Your choice goes under “Assumptions” in the description.</Hint>
        </div>
      )}
      {proposal && (
        <p className="m-0 text-xs text-fg-subtle">
          {proposal.providerName} · {proposal.model} · {proposal.promptVersion} ·{' '}
          {Math.round(proposal.durationMs / 1000)} s
        </p>
      )}
    </section>
  )
}

/**
 * Under a field the refine changed: what it was, and a button that puts it back. `children` names the change when
 * there is no old text to show.
 */
export function Suggestion({
  was,
  label,
  onDrop,
  children,
}: {
  was?: string
  label: string
  onDrop: () => void
  children?: ReactNode
}) {
  return (
    <p className="m-0 flex min-w-0 items-center gap-2 text-sm text-fg-muted">
      <Tag tone="wait">AI</Tag>
      <span className="min-w-0 flex-1 [overflow-wrap:anywhere]">
        {children}
        {was !== undefined && (
          <>
            was: <s className="text-fg-subtle">{was || '(empty)'}</s>
          </>
        )}
      </span>
      <button
        type="button"
        className="flex size-6 shrink-0 cursor-pointer items-center justify-center rounded-md text-fg-subtle hover:bg-hover hover:text-fg"
        aria-label={label}
        title={label}
        onClick={onDrop}
      >
        <XIcon size={13} />
      </button>
    </p>
  )
}
