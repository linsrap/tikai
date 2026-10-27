import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, ensureAntiforgery } from '../api/client'
import type { components } from '../api/schema'
import { unwrap } from '../api/result'

export type RunInfo = components['schemas']['RunInfo']
export type RunState = components['schemas']['AgentRunState']
export type RunEventInfo = components['schemas']['RunEventInfo']
export type StartRunRequest = components['schemas']['StartRunRequest']
export type WaitingItem = components['schemas']['WaitingItem']

export const runStateLabel: Record<RunState, string> = {
  queued: 'Queued',
  preparing: 'Preparing',
  running: 'Running',
  blocked: 'Waiting for an answer',
  in_review: 'In review',
  changes_requested: 'Changes requested',
  merged: 'Merged',
  failed: 'Failed',
  cancelled: 'Cancelled',
}

/** States in which the run has not ended (it can be cancelled). */
export const isActive = (state: RunState) => !['merged', 'failed', 'cancelled'].includes(state)

/** "1:05:09" or "4:02": how long the run has been going, or went (ended runs). */
export function elapsed(
  run: Pick<RunInfo, 'startedAt' | 'createdAt' | 'endedAt'>,
  now = Date.now(),
) {
  const from = new Date(run.startedAt ?? run.createdAt).getTime()
  const to = run.endedAt ? new Date(run.endedAt).getTime() : now
  const total = Math.max(0, Math.floor((to - from) / 1000))
  const [h, m, s] = [Math.floor(total / 3600), Math.floor((total % 3600) / 60), total % 60]
  const two = (n: number) => String(n).padStart(2, '0')
  return h > 0 ? `${h}:${two(m)}:${two(s)}` : `${m}:${two(s)}`
}

/** Cost and tokens as the agent reported them when a container ended; nothing while the first one runs. */
export function costText(run: Pick<RunInfo, 'costUsd' | 'costTokens'>): string {
  if (run.costUsd == null) return 'reported when the agent finishes'
  const tokens = run.costTokens != null ? `, ${run.costTokens.toLocaleString()} tokens` : ''
  return `${run.costUsd.toFixed(4)} USD${tokens}`
}

/** What the agent waits for, in one line. */
export function waitingText(item: WaitingItem): string {
  if (item.question) {
    const options =
      item.question.options.length > 0 ? ` (${item.question.options.join(' / ')})` : ''
    return `Question: ${item.question.question}${options}`
  }
  if (item.plan) {
    const n = item.plan.steps.length
    return `Plan with ${n} step${n === 1 ? '' : 's'}: ${item.plan.steps.join('; ')}`
  }
  return item.kind
}

/** What happens to a blocked run's agent; null when the run is not blocked. */
export function blockedNote(
  run: Pick<RunInfo, 'state' | 'agentStopped' | 'blockTimeoutAt' | 'waiting'>,
  time: (iso: string) => string,
): string | null {
  if (run.state !== 'blocked') return null
  if (run.agentStopped) {
    return run.waiting.length > 0
      ? 'The agent was stopped while it waited. It continues in a new container once everything is answered.'
      : 'Everything is answered; the agent is being resumed in a new container.'
  }
  return run.blockTimeoutAt
    ? `The agent waits until ${time(run.blockTimeoutAt)}. Then it is stopped, and it continues in a new container once answered.`
    : null
}

/** "Resumed 2×, last in its own session"; null while the run is on its first container. */
export function resumeLabel(run: Pick<RunInfo, 'attempt' | 'attemptKind'>): string | null {
  if (run.attempt <= 1) return null
  const how = run.attemptKind === 'resume' ? 'its own session' : 'a fresh session with a summary'
  return `Resumed ${run.attempt - 1}×, last in ${how}`
}

/** Only the choices the person made; the server picks the defaults (the only repository, the project's provider). */
export function toStartRunRequest(
  repositoryId: string | null,
  providerId: string | null,
): StartRunRequest {
  return { repositoryId, providerId }
}

type Payload = Record<string, unknown>

const text = (value: unknown) =>
  typeof value === 'string' ? value : value == null ? '' : JSON.stringify(value)

/** One line for a transcript entry; the full payload is shown on demand. */
export function eventSummary(event: RunEventInfo): string {
  const p = (event.payload ?? {}) as Payload
  switch (event.kind) {
    case 'system':
      return `Started (${text(p.model)}, Claude Code ${text(p.claude_code_version)}); MCP: ${
        Array.isArray(p.mcp_servers)
          ? p.mcp_servers.map((s: Payload) => `${text(s.name)} ${text(s.status)}`).join(', ')
          : 'unknown'
      }`
    case 'message':
      return p.thinking !== undefined ? `(thinking) ${text(p.thinking)}` : text(p.text)
    case 'tool_call':
      return `${text(p.name)} ${text(p.input)}`
    case 'tool_result':
      return `${p.is_error ? 'Error: ' : '→ '}${text(p.content)}`
    case 'progress':
      return `Progress: ${text(p.step)}${p.note ? ` (${text(p.note)})` : ''}`
    case 'result':
      return `Result: ${text(p.subtype)}${p.is_error ? ' (error)' : ''}, ${text(p.num_turns)} turns`
    case 'log':
      return `${p.source === 'hub' ? 'Hub' : text(p.stream) || 'Agent'}: ${text(p.text)}${p.reason ? ` (${text(p.reason)})` : ''}`
  }
}

export const runsQuery = (slug: string, number: number) =>
  queryOptions({
    queryKey: ['projects', slug, 'tickets', number, 'runs'],
    queryFn: () =>
      unwrap(
        api.GET('/api/projects/{slug}/tickets/{number}/runs', {
          params: { path: { slug, number } },
        }),
        'load the runs',
      ),
    // Kept fresh by live updates (the page watches its project), not by polling.
  })

/** The agent board: runs that have not ended, and those that ended within the hour. Kept fresh by live updates. */
export const boardQuery = queryOptions({
  queryKey: ['runs', 'board'],
  queryFn: () => unwrap(api.GET('/api/runs'), 'load the agent board'),
})

function useRunMutation<TInput, TResult>(slug: string, send: (input: TInput) => Promise<TResult>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (input: TInput) => {
      await ensureAntiforgery()
      return send(input)
    },
    // The ticket's status follows its run.
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['projects', slug, 'tickets'] })
      void queryClient.invalidateQueries({ queryKey: ['runs'] })
      void queryClient.invalidateQueries({ queryKey: ['attention-items'] })
    },
  })
}

/** Assigns the ticket to the agent: starts a run. */
export const useStartRun = (slug: string, number: number) =>
  useRunMutation(slug, (body: StartRunRequest) =>
    unwrap(
      api.POST('/api/projects/{slug}/tickets/{number}/runs', {
        params: { path: { slug, number } },
        body,
      }),
      'assign the ticket to the agent',
    ),
  )

export const useCancelRun = (slug: string) =>
  useRunMutation(slug, (id: string) =>
    unwrap(api.POST('/api/runs/{id}/cancel', { params: { path: { id } } }), 'cancel the run'),
  )
