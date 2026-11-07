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
import { Button } from '../ui/Button'
import { ErrorText, Hint, Select } from '../ui/form'
import { CheckIcon } from '../ui/icons'
import { ConfirmDialog } from '../ui/overlay'

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
      <Hint>
        To refine tickets with AI, add an LLM provider to the{' '}
        <Link to="/projects/$slug" params={{ slug }}>
          project
        </Link>{' '}
        or your own under <Link to="/settings">Settings</Link>.
      </Hint>
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
    <section
      className="flex flex-col gap-4 rounded-lg border border-line bg-surface p-4"
      aria-label="Refine with AI"
    >
      <div className="flex flex-wrap items-center gap-3">
        <div className="min-w-0 flex-1">
          <h2 className="m-0 text-sm font-semibold">Refine with AI</h2>
          <Hint>
            Proposes a clearer title, description and criteria. Nothing changes until you take it
            over.
          </Hint>
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
        <Button disabled={refine.isPending || !form.title.trim()} onClick={start}>
          {refine.isPending ? 'Refining… (can take a minute)' : 'Refine with AI'}
        </Button>
      </div>
      {refine.error && <ErrorText>{refine.error.message}</ErrorText>}
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
    <div className="flex flex-col gap-5 border-t border-line-soft pt-4">
      <p className="m-0 text-sm text-fg-subtle">
        Proposal from {proposal.providerName} ({proposal.model}, {proposal.promptVersion}) in{' '}
        {Math.round(proposal.durationMs / 1000)} s. Take over what you want, then save.
      </p>
      {title && title !== form.title && (
        <ProposalPart title="Title">
          <p className="m-0 font-medium">{title}</p>
          <TakeButton
            done={used.has('title')}
            onClick={() => take('title', takeProposedTitle(form, title))}
          >
            Use this title
          </TakeButton>
        </ProposalPart>
      )}
      {description && (
        <ProposalPart title="Description">
          <div className="rounded-md border border-line-soft bg-bg px-4 py-3">
            <MarkdownView text={description} />
          </div>
          <TakeButton
            done={used.has('description')}
            onClick={() => take('description', takeProposedDescription(form, description))}
          >
            Replace the description
          </TakeButton>
        </ProposalPart>
      )}
      {proposal.criteria.length > 0 && (
        <ProposalPart title="Acceptance criteria">
          <ol className="m-0 flex list-none flex-col gap-1.5 p-0">
            {proposal.criteria.map((c, i) => (
              <li key={c} className="flex items-baseline gap-3">
                <span className="w-5 shrink-0 text-right text-sm text-fg-subtle tabular-nums">
                  {i + 1}.
                </span>
                <span className="min-w-0 flex-1">{c}</span>
                <TakeButton
                  done={used.has(`criterion-${i}`) || used.has('criteria')}
                  onClick={() => take(`criterion-${i}`, addProposedCriterion(form, c))}
                >
                  Add
                </TakeButton>
              </li>
            ))}
          </ol>
          {used.has('criteria') ? (
            <TakenOver />
          ) : hasSavedCriteria ? (
            <ConfirmDialog
              trigger={<Button size="sm">Replace all criteria with these</Button>}
              title="Replace all criteria?"
              description="Saved criteria are deleted and the proposed ones added as new."
              confirmLabel="Replace criteria"
              onConfirm={() => take('criteria', replaceCriteria(form, proposal.criteria))}
            />
          ) : (
            <TakeButton
              done={false}
              onClick={() => take('criteria', replaceCriteria(form, proposal.criteria))}
            >
              Replace all criteria with these
            </TakeButton>
          )}
        </ProposalPart>
      )}
      {proposal.questions.length > 0 && (
        <ProposalPart title="Open questions">
          <ul className="m-0 list-disc space-y-1 pl-5 marker:text-fg-faint">
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
        </ProposalPart>
      )}
      <div>
        <Button variant="ghost" onClick={onDismiss}>
          Close the proposal
        </Button>
      </div>
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
    <TakenOver />
  ) : (
    <div>
      <Button size="sm" onClick={onClick}>
        {children}
      </Button>
    </div>
  )
}

function TakenOver() {
  return (
    <span className="inline-flex h-7 items-center gap-1.5 text-xs font-medium text-ok">
      <CheckIcon size={13} strokeWidth={2} />
      Taken over
    </span>
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
