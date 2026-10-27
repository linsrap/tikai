import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, ensureAntiforgery } from '../api/client'
import type { components } from '../api/schema'
import { unwrap } from '../api/result'

export type RunInfo = components['schemas']['RunInfo']
export type RunState = components['schemas']['AgentRunState']
export type RunEventInfo = components['schemas']['RunEventInfo']
export type StartRunRequest = components['schemas']['StartRunRequest']

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

/** States in which the run still changes by itself (so the page polls). */
export const isMoving = (state: RunState) => ['queued', 'preparing', 'running'].includes(state)

/** States in which the run has not ended (it can be cancelled). */
export const isActive = (state: RunState) => !['merged', 'failed', 'cancelled'].includes(state)

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
    // Poll while a run moves by itself; live updates come with SignalR (roadmap step 12).
    refetchInterval: (query) => (query.state.data?.some((r) => isMoving(r.state)) ? 3000 : false),
  })

export const runEventsQuery = (id: string, moving: boolean) =>
  queryOptions({
    queryKey: ['runs', id, 'events'],
    queryFn: () =>
      unwrap(
        api.GET('/api/runs/{id}/events', { params: { path: { id }, query: { limit: 1000 } } }),
        'load the transcript',
      ),
    refetchInterval: moving ? 3000 : false,
  })

function useRunMutation<TInput, TResult>(slug: string, send: (input: TInput) => Promise<TResult>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (input: TInput) => {
      await ensureAntiforgery()
      return send(input)
    },
    // The ticket's status follows its run.
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['projects', slug, 'tickets'] }),
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
