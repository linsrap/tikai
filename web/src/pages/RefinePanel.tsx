import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { useState, type ReactNode } from 'react'
import { orderedOptions } from '../inbox/inbox'
import { providersQuery } from '../llm/llm'
import { MarkdownView } from '../tickets/MarkdownView'
import {
  applyProposal,
  largerSize,
  refinedCriterionKeys,
  selectAll,
  toRefineSpecRequest,
  useRefineSpec,
  type RefineSelection,
  type SpecProposal,
  type SpecSize,
  type TicketForm,
} from '../tickets/tickets'
import { Button } from '../ui/Button'
import { Check, ErrorText, Hint, Select } from '../ui/form'
import { CheckIcon } from '../ui/icons'
import { Tag } from '../ui/Tag'

/**
 * "Refine with AI": sends the form as it is to an LLM and shows only what it would change, each part ticked. One
 * click takes over what stays ticked; the form is saved only by its own Save button.
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
  // The criteria the request sent, by key: a rewrite names its criterion by its place in this list.
  const [keys, setKeys] = useState<string[]>([])
  const [taken, setTaken] = useState(false)
  const proposal = refine.data

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

  function start(minSize?: SpecSize) {
    setTaken(false)
    setKeys(refinedCriterionKeys(form))
    refine.mutate(toRefineSpecRequest(form, providerId, minSize))
  }

  return (
    <section
      className="flex flex-col gap-4 rounded-lg border border-line bg-surface p-4"
      aria-label="Refine with AI"
    >
      <div className="flex flex-wrap items-center gap-3">
        <div className="min-w-0 flex-1">
          <h2 className="m-0 text-sm font-semibold">Refine with AI</h2>
          <Hint>Proposes only what it would change. Nothing changes until you take it over.</Hint>
        </div>
        {providers.length > 1 && (
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
        <Button disabled={refine.isPending || !form.title.trim()} onClick={() => start()}>
          {refine.isPending ? 'Refining… (can take a minute)' : 'Refine with AI'}
        </Button>
      </div>
      {refine.error && <ErrorText>{refine.error.message}</ErrorText>}
      {taken && (
        <p className="m-0 inline-flex items-center gap-1.5 text-sm font-medium text-ok">
          <CheckIcon size={13} strokeWidth={2} />
          Taken over. Save the ticket to keep it.
        </p>
      )}
      {proposal && !taken && (
        <Proposal
          // A new answer starts with everything ticked again.
          key={`${proposal.size}-${proposal.durationMs}-${proposal.promptVersion}`}
          proposal={proposal}
          pending={refine.isPending}
          onMore={(size) => start(size)}
          onTake={(selection) => {
            onChange(applyProposal(form, proposal, selection, keys))
            setTaken(true)
          }}
          onDismiss={() => refine.reset()}
        />
      )}
    </section>
  )
}

function Proposal({
  proposal,
  pending,
  onMore,
  onTake,
  onDismiss,
}: {
  proposal: SpecProposal
  pending: boolean
  onMore: (size: SpecSize) => void
  onTake: (selection: RefineSelection) => void
  onDismiss: () => void
}) {
  const [selection, setSelection] = useState<RefineSelection>(() => selectAll(proposal))
  const more = largerSize(proposal.size)
  const count =
    (selection.title ? 1 : 0) +
    (selection.description ? 1 : 0) +
    selection.criteria.length +
    selection.assumptions.length +
    selection.decisions.length
  const empty =
    proposal.title == null &&
    proposal.description == null &&
    proposal.criteria.length === 0 &&
    proposal.assumptions.length === 0 &&
    proposal.questions.length === 0

  const toggle = (list: readonly number[], i: number, on: boolean) =>
    on ? [...list.filter((x) => x !== i), i].sort((a, b) => a - b) : list.filter((x) => x !== i)
  const decide = (question: string, option: string | null) =>
    setSelection((s) => ({
      ...s,
      decisions: [
        ...s.decisions.filter((d) => d.question !== question),
        ...(option ? [{ question, option }] : []),
      ],
    }))

  return (
    <div className="flex flex-col gap-5 border-t border-line-soft pt-4">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <Tag tone="neutral">{proposal.size}</Tag>
        <p className="m-0 min-w-0 flex-1 text-sm text-fg-subtle">
          Proposal from {proposal.providerName} ({proposal.model}, {proposal.promptVersion}) in{' '}
          {Math.round(proposal.durationMs / 1000)} s.
        </p>
        {more && (
          <Button size="sm" variant="ghost" disabled={pending} onClick={() => onMore(more)}>
            {pending ? 'Asking…' : 'More detail'}
          </Button>
        )}
      </div>
      {empty && <p className="m-0 text-fg-2">The ticket is fine as it is.</p>}
      {proposal.title != null && (
        <ProposalPart title="Title">
          <Check
            checked={selection.title}
            onChange={(e) => setSelection({ ...selection, title: e.target.checked })}
            label={<span className="font-medium">{proposal.title}</span>}
          />
        </ProposalPart>
      )}
      {proposal.description != null && (
        <ProposalPart title="Description">
          <Check
            checked={selection.description}
            onChange={(e) => setSelection({ ...selection, description: e.target.checked })}
            label="Replace the description with this"
          />
          <div className="rounded-md border border-line-soft bg-bg px-4 py-3">
            <MarkdownView text={proposal.description} />
          </div>
        </ProposalPart>
      )}
      {proposal.criteria.length > 0 && (
        <ProposalPart title="Acceptance criteria">
          {proposal.criteria.map((c, i) => (
            <Check
              key={i}
              checked={selection.criteria.includes(i)}
              onChange={(e) =>
                setSelection({
                  ...selection,
                  criteria: toggle(selection.criteria, i, e.target.checked),
                })
              }
              label={
                <>
                  <span className="mr-2 text-xs font-semibold text-fg-muted">
                    {c.was != null ? 'Changed' : 'New'}
                  </span>
                  {c.text}
                </>
              }
              hint={c.was != null ? `was: ${c.was}` : undefined}
            />
          ))}
        </ProposalPart>
      )}
      {proposal.assumptions.length > 0 && (
        <ProposalPart title="Assumptions">
          <Hint>
            Accepted unless you untick them; they go under “Assumptions” in the description.
          </Hint>
          {proposal.assumptions.map((a, i) => (
            <Check
              key={i}
              checked={selection.assumptions.includes(i)}
              onChange={(e) =>
                setSelection({
                  ...selection,
                  assumptions: toggle(selection.assumptions, i, e.target.checked),
                })
              }
              label={a}
            />
          ))}
        </ProposalPart>
      )}
      {proposal.questions.length > 0 && (
        <ProposalPart title="Questions">
          {proposal.questions.map((q) => {
            const chosen = selection.decisions.find((d) => d.question === q.question)?.option
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
                      onClick={() => decide(q.question, chosen === option ? null : option)}
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
        </ProposalPart>
      )}
      <div className="flex flex-wrap items-center gap-2">
        {!empty && (
          <Button variant="primary" disabled={count === 0} onClick={() => onTake(selection)}>
            Take over selected ({count})
          </Button>
        )}
        <Button variant="ghost" onClick={onDismiss}>
          Close the proposal
        </Button>
      </div>
    </div>
  )
}

function ProposalPart({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="flex flex-col items-start gap-2 [&>*]:max-w-full">
      <h3 className="m-0 text-xs font-semibold text-fg-muted">{title}</h3>
      {children}
    </div>
  )
}
