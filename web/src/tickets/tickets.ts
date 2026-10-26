import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, ensureAntiforgery } from '../api/client'
import type { components } from '../api/schema'
import { unwrap } from '../api/result'

export type TicketSummary = components['schemas']['TicketSummary']
export type TicketInfo = components['schemas']['TicketInfo']
export type TicketStatus = components['schemas']['TicketStatus']
export type TicketPriority = components['schemas']['TicketPriority']
export type CreateTicketRequest = components['schemas']['CreateTicketRequest']
export type UpdateTicketRequest = components['schemas']['UpdateTicketRequest']
export type RefineSpecRequest = components['schemas']['RefineSpecRequest']
export type SpecProposal = components['schemas']['SpecProposalInfo']

export const statusLabel: Record<TicketStatus, string> = {
  open: 'Open',
  in_progress: 'In progress',
  in_review: 'In review',
  done: 'Done',
  closed: 'Closed',
}

export const priorityLabel: Record<TicketPriority, string> = {
  low: 'Low',
  normal: 'Normal',
  high: 'High',
  urgent: 'Urgent',
}

/** Filters of the ticket list. "Active" is everything not finished yet. */
export const ticketFilters = {
  active: ['open', 'in_progress', 'in_review'],
  done: ['done'],
  closed: ['closed'],
  all: ['open', 'in_progress', 'in_review', 'done', 'closed'],
} satisfies Record<string, TicketStatus[]>

export type TicketFilter = keyof typeof ticketFilters

/** A criterion in the form. `key` only identifies the row on screen; `id` is null until the criterion is saved. */
export type CriterionDraft = { key: string; id: string | null; text: string }

export type TicketForm = {
  title: string
  description: string
  status: TicketStatus
  priority: TicketPriority
  criteria: CriterionDraft[]
}

let nextDraftKey = 0
const draftKey = () => `new-${++nextDraftKey}`

export const emptyTicketForm = (): TicketForm => ({
  title: '',
  description: '',
  status: 'open',
  priority: 'normal',
  criteria: [],
})

export function toTicketForm(ticket: TicketInfo): TicketForm {
  return {
    title: ticket.title,
    description: ticket.description,
    status: ticket.status,
    priority: ticket.priority,
    criteria: ticket.criteria.map((c) => ({ key: c.id, id: c.id, text: c.text })),
  }
}

export function addCriterion(criteria: CriterionDraft[]): CriterionDraft[] {
  return [...criteria, { key: draftKey(), id: null, text: '' }]
}

export function removeCriterion(criteria: CriterionDraft[], index: number): CriterionDraft[] {
  return criteria.filter((_, i) => i !== index)
}

export function changeCriterion(
  criteria: CriterionDraft[],
  index: number,
  text: string,
): CriterionDraft[] {
  return criteria.map((c, i) => (i === index ? { ...c, text } : c))
}

/** Moves a criterion up (-1) or down (+1); does nothing at either end. */
export function moveCriterion(
  criteria: CriterionDraft[],
  index: number,
  by: -1 | 1,
): CriterionDraft[] {
  const target = index + by
  if (target < 0 || target >= criteria.length) {
    return criteria
  }
  const moved = [...criteria]
  ;[moved[index], moved[target]] = [moved[target], moved[index]]
  return moved
}

/** Rows left empty are not sent: an emptied criterion is removed. */
const filled = (criteria: CriterionDraft[]) => criteria.filter((c) => c.text.trim() !== '')

export function toCreateTicketRequest(form: TicketForm): CreateTicketRequest {
  return {
    title: form.title.trim(),
    description: form.description,
    priority: form.priority,
    criteria: filled(form.criteria).map((c) => c.text.trim()),
  }
}

export function toUpdateTicketRequest(form: TicketForm): UpdateTicketRequest {
  return {
    title: form.title.trim(),
    description: form.description,
    status: form.status,
    priority: form.priority,
    criteria: filled(form.criteria).map((c) => ({ id: c.id, text: c.text.trim() })),
  }
}

/** The draft as it is on screen goes to the model; empty criterion rows are left out. */
export function toRefineSpecRequest(
  form: TicketForm,
  providerId: string | null,
): RefineSpecRequest {
  return {
    title: form.title.trim(),
    description: form.description,
    criteria: filled(form.criteria).map((c) => c.text.trim()),
    providerId,
  }
}

// Taking over parts of a proposal. Each one changes only the form; nothing is saved until "Save".

export function takeProposedTitle(form: TicketForm, title: string): TicketForm {
  return { ...form, title }
}

export function takeProposedDescription(form: TicketForm, description: string): TicketForm {
  return { ...form, description }
}

/** Adds one proposed criterion after the existing ones. */
export function addProposedCriterion(form: TicketForm, text: string): TicketForm {
  return { ...form, criteria: [...form.criteria, { key: draftKey(), id: null, text }] }
}

/** Replaces all criteria; replaced criteria get new ids when saved. */
export function replaceCriteria(form: TicketForm, texts: string[]): TicketForm {
  return { ...form, criteria: texts.map((text) => ({ key: draftKey(), id: null, text })) }
}

/** Appends the open questions to the description, so they can be answered there before refining again. */
export function appendQuestions(form: TicketForm, questions: string[]): TicketForm {
  const list = questions.map((q) => `- ${q}`).join('\n')
  const description = form.description.trimEnd()
  return {
    ...form,
    description: `${description}${description ? '\n\n' : ''}## Open questions\n\n${list}\n`,
  }
}

export const ticketsQuery = (slug: string) =>
  queryOptions({
    queryKey: ['projects', slug, 'tickets'],
    queryFn: () =>
      unwrap(
        api.GET('/api/projects/{slug}/tickets', { params: { path: { slug } } }),
        'load the tickets',
      ),
  })

export const ticketQuery = (slug: string, number: number) =>
  queryOptions({
    queryKey: ['projects', slug, 'tickets', number],
    queryFn: () =>
      unwrap(
        api.GET('/api/projects/{slug}/tickets/{number}', {
          params: { path: { slug, number } },
        }),
        'load the ticket',
      ),
  })

function useTicketMutation<TInput, TResult>(
  slug: string,
  send: (input: TInput) => Promise<TResult>,
) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (input: TInput) => {
      await ensureAntiforgery()
      return send(input)
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['projects', slug, 'tickets'] }),
  })
}

export const useCreateTicket = (slug: string) =>
  useTicketMutation(slug, (body: CreateTicketRequest) =>
    unwrap(
      api.POST('/api/projects/{slug}/tickets', { params: { path: { slug } }, body }),
      'create the ticket',
    ),
  )

export const useUpdateTicket = (slug: string, number: number) =>
  useTicketMutation(slug, (body: UpdateTicketRequest) =>
    unwrap(
      api.PUT('/api/projects/{slug}/tickets/{number}', {
        params: { path: { slug, number } },
        body,
      }),
      'save the ticket',
    ),
  )

export const useDeleteTicket = (slug: string, number: number) =>
  useTicketMutation(slug, () =>
    unwrap(
      api.DELETE('/api/projects/{slug}/tickets/{number}', {
        params: { path: { slug, number } },
      }),
      'delete the ticket',
    ),
  )

/** Asks the LLM for a proposal. Costs tokens on the provider's key; stores nothing. */
export const useRefineSpec = (slug: string) =>
  useMutation({
    mutationFn: async (body: RefineSpecRequest) => {
      await ensureAntiforgery()
      return unwrap(
        api.POST('/api/projects/{slug}/refine-spec', { params: { path: { slug } }, body }),
        'refine the ticket',
      )
    },
  })
