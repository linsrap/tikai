import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { useState, type ReactNode } from 'react'
import { providersQuery } from '../llm/llm'
import { MarkdownView } from '../tickets/MarkdownView'
import {
  addProposedCriterion,
  appendQuestions,
  replaceCriteria,
  takeProposedDescription,
  takeProposedTitle,
  toRefineSpecRequest,
  useRefineSpec,
  type SpecProposal,
  type TicketForm,
} from '../tickets/tickets'

/**
 * "Refine with AI": sends the form as it is to an LLM and shows the proposal next to it.
 * Each part is taken over only by clicking it; the form is saved only by its own Save button.
 */
export function RefinePanel({
  slug,
  form,
  onChange,
}: {
  slug: string
  form: TicketForm
  onChange: (form: TicketForm) => void
}) {
  const projectProviders = useQuery(providersQuery({ kind: 'project', slug }))
  const ownProviders = useQuery(providersQuery({ kind: 'user' }))
  // The server prefers the project's providers too; this list shows them first.
  const providers = [...(projectProviders.data ?? []), ...(ownProviders.data ?? [])]
  const [providerId, setProviderId] = useState<string | null>(null)
  const refine = useRefineSpec(slug)
  const [used, setUsed] = useState<Set<string>>(new Set())
  const proposal = refine.data

  if (projectProviders.isPending || ownProviders.isPending) {
    return null
  }
  if (providers.length === 0) {
    return (
      <p className="muted">
        To refine tickets with AI, add an LLM provider to the{' '}
        <Link to="/projects/$slug" params={{ slug }}>
          project
        </Link>{' '}
        or your own under <Link to="/settings">Settings</Link>.
      </p>
    )
  }

  function take(part: string, next: TicketForm) {
    onChange(next)
    setUsed(new Set(used).add(part))
  }

  function start() {
    setUsed(new Set())
    refine.mutate(toRefineSpecRequest(form, providerId))
  }

  return (
    <section className="card stack" aria-label="Refine with AI">
      <div className="row">
        <button type="button" disabled={refine.isPending || !form.title.trim()} onClick={start}>
          {refine.isPending ? 'Refining… (can take a minute)' : 'Refine with AI'}
        </button>
        {providers.length > 1 && (
          <label className="row">
            <span className="muted">using</span>
            <select
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
            </select>
          </label>
        )}
      </div>
      {refine.error && (
        <p role="alert" className="error">
          {refine.error.message}
        </p>
      )}
      {proposal && (
        <Proposal
          proposal={proposal}
          form={form}
          used={used}
          take={take}
          onDismiss={() => refine.reset()}
        />
      )}
    </section>
  )
}

function Proposal({
  proposal,
  form,
  used,
  take,
  onDismiss,
}: {
  proposal: SpecProposal
  form: TicketForm
  used: Set<string>
  take: (part: string, next: TicketForm) => void
  onDismiss: () => void
}) {
  const hasSavedCriteria = form.criteria.some((c) => c.id !== null)
  const { title, description } = proposal

  return (
    <div className="stack proposal">
      <p className="muted">
        Proposal from {proposal.providerName} ({proposal.model}, {proposal.promptVersion}) in{' '}
        {Math.round(proposal.durationMs / 1000)} s. Take over what you want, then save.
      </p>
      {title && title !== form.title && (
        <div className="stack">
          <strong>Title</strong>
          <p>{title}</p>
          <TakeButton
            done={used.has('title')}
            onClick={() => take('title', takeProposedTitle(form, title))}
          >
            Use this title
          </TakeButton>
        </div>
      )}
      {description && (
        <div className="stack">
          <strong>Description</strong>
          <div className="card">
            <MarkdownView text={description} />
          </div>
          <TakeButton
            done={used.has('description')}
            onClick={() => take('description', takeProposedDescription(form, description))}
          >
            Replace the description
          </TakeButton>
        </div>
      )}
      {proposal.criteria.length > 0 && (
        <div className="stack">
          <strong>Acceptance criteria</strong>
          <ol className="list">
            {proposal.criteria.map((c, i) => (
              <li key={c}>
                {c}{' '}
                <TakeButton
                  done={used.has(`criterion-${i}`) || used.has('criteria')}
                  onClick={() => take(`criterion-${i}`, addProposedCriterion(form, c))}
                >
                  Add
                </TakeButton>
              </li>
            ))}
          </ol>
          <TakeButton
            done={used.has('criteria')}
            onClick={() => {
              if (
                !hasSavedCriteria ||
                window.confirm(
                  'Replace all criteria? Saved criteria are deleted and the proposed ones added as new.',
                )
              ) {
                take('criteria', replaceCriteria(form, proposal.criteria))
              }
            }}
          >
            Replace all criteria with these
          </TakeButton>
        </div>
      )}
      {proposal.questions.length > 0 && (
        <div className="stack">
          <strong>Open questions</strong>
          <ul className="list">
            {proposal.questions.map((q) => (
              <li key={q}>{q}</li>
            ))}
          </ul>
          <TakeButton
            done={used.has('questions')}
            onClick={() => take('questions', appendQuestions(form, proposal.questions))}
          >
            Add to the description to answer them
          </TakeButton>
        </div>
      )}
      <p>
        <button type="button" onClick={onDismiss}>
          Close the proposal
        </button>
      </p>
    </div>
  )
}

function TakeButton({
  done,
  onClick,
  children,
}: {
  done: boolean
  onClick: () => void
  children: ReactNode
}) {
  return done ? (
    <span className="success">✓ Taken over</span>
  ) : (
    <button type="button" onClick={onClick}>
      {children}
    </button>
  )
}
