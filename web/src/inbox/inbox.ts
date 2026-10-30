import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, ensureAntiforgery } from '../api/client'
import type { components } from '../api/schema'
import { unwrap } from '../api/result'

export type AttentionItem = components['schemas']['AttentionItemInfo']
export type AttentionKind = components['schemas']['AttentionKind']
export type AnswerRequest = components['schemas']['AnswerRequest']
export type ReviewCoverage = components['schemas']['ReviewCoverage']
export type AnalysisBrief = components['schemas']['AnalysisBrief']

export const kindLabel: Record<AttentionKind, string> = {
  question: 'Question',
  permission: 'Permission',
  plan_approval: 'Plan to approve',
  review: 'Ready for review',
  conflict: 'Conflict',
  failure: 'Run failed',
}

/** Open items, those an agent waits for first (the server sorts). Kept fresh by live updates. */
export const inboxQuery = queryOptions({
  queryKey: ['attention-items', 'open'],
  queryFn: () => unwrap(api.GET('/api/attention-items'), 'load the inbox'),
})

/** How many open items an agent waits for: the count in the top bar. */
export const waitingCount = (items: readonly Pick<AttentionItem, 'blocking'>[] | undefined) =>
  items?.filter((i) => i.blocking).length ?? 0

/** "2 covered, 1 partial, 1 missing" for a review's criteria. */
export function coverageSummary(coverage: readonly Pick<ReviewCoverage, 'status'>[]): string {
  const counts = { covered: 0, partial: 0, missing: 0 }
  for (const c of coverage) counts[c.status]++
  return (['covered', 'partial', 'missing'] as const)
    .filter((s) => counts[s] > 0)
    .map((s) => `${counts[s]} ${s}`)
    .join(', ')
}

/** The review analysis in one line for the inbox card. */
export function analysisLine(brief: AnalysisBrief): string {
  if (brief.status === 'pending') return 'Analysis: running…'
  if (brief.status === 'failed')
    return `Analysis unavailable${brief.error ? `: ${brief.error}` : ''}`
  const coverage = (['covered', 'partial', 'missing'] as const)
    .filter((status) => brief[status] > 0)
    .map((status) => `${brief[status]} ${status}`)
    .join(', ')
  const risk = brief.highRisk === 1 ? '1 high-risk file' : `${brief.highRisk} high-risk files`
  return `Analysis: ${coverage || 'no criteria assessed'} · ${risk}`
}

/** "3 min ago", "2 h ago", or the date for older items. */
export function ago(iso: string, now = Date.now()): string {
  const seconds = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000))
  if (seconds < 60) return 'just now'
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ago`
  return new Date(iso).toLocaleString()
}

function useItemMutation<TInput>(send: (input: TInput) => Promise<unknown>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (input: TInput) => {
      await ensureAntiforgery()
      return send(input)
    },
    // Live updates refresh the others too; the person who acted sees it at once. A 409 (answered elsewhere) also re-reads.
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: ['attention-items'] })
      void queryClient.invalidateQueries({ queryKey: ['runs'] })
      void queryClient.invalidateQueries({ queryKey: ['projects'] })
    },
  })
}

export const useAnswer = (id: string) =>
  useItemMutation((body: AnswerRequest) =>
    unwrap(
      api.POST('/api/attention-items/{id}/answer', { params: { path: { id } }, body }),
      'send the answer',
    ),
  )

export const useDismiss = (id: string) =>
  useItemMutation(() =>
    unwrap(
      api.POST('/api/attention-items/{id}/dismiss', { params: { path: { id } } }),
      'dismiss the item',
    ),
  )

export const useRetry = (id: string) =>
  useItemMutation(() =>
    unwrap(
      api.POST('/api/attention-items/{id}/retry', { params: { path: { id } } }),
      'retry the run',
    ),
  )
