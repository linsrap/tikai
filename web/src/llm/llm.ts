import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, ensureAntiforgery } from '../api/client'
import type { components } from '../api/schema'
import { unwrap } from '../api/result'

export type LlmProviderInfo = components['schemas']['LlmProviderInfo']
export type LlmTestResponse = components['schemas']['LlmTestResponse']
export type CreateLlmProviderRequest = components['schemas']['CreateLlmProviderRequest']
export type UpdateLlmProviderRequest = components['schemas']['UpdateLlmProviderRequest']

/** The model suggested for new providers: the current Claude Opus. */
export const defaultModel = 'claude-opus-5-5'

/** Whose providers a list shows: the logged-in user's own, or a project's. */
export type ProviderOwner = { kind: 'user' } | { kind: 'project'; slug: string }

export type ProviderForm = { name: string; defaultModel: string; apiKey: string; baseUrl: string }

export const emptyProviderForm: ProviderForm = {
  name: 'Anthropic',
  defaultModel,
  apiKey: '',
  baseUrl: '',
}

export function toCreateRequest(form: ProviderForm): CreateLlmProviderRequest {
  return {
    name: form.name.trim(),
    defaultModel: form.defaultModel.trim(),
    apiKey: form.apiKey.trim(),
    kind: 'anthropic',
    baseUrl: form.baseUrl.trim() || null,
  }
}

/** An empty key field keeps the stored key. */
export function toUpdateRequest(form: ProviderForm): UpdateLlmProviderRequest {
  return {
    name: form.name.trim(),
    defaultModel: form.defaultModel.trim(),
    baseUrl: form.baseUrl.trim() || null,
    apiKey: form.apiKey.trim() || null,
  }
}

const ownerKey = (owner: ProviderOwner) =>
  owner.kind === 'user' ? ['llm-providers'] : ['projects', owner.slug, 'llm-providers']

export const providersQuery = (owner: ProviderOwner) =>
  queryOptions({
    queryKey: ownerKey(owner),
    queryFn: () =>
      owner.kind === 'user'
        ? unwrap(api.GET('/api/llm-providers'), 'load the providers')
        : unwrap(
            api.GET('/api/projects/{slug}/llm-providers', {
              params: { path: { slug: owner.slug } },
            }),
            'load the providers',
          ),
  })

function useProviderMutation<TInput, TResult>(
  owner: ProviderOwner,
  send: (input: TInput) => Promise<TResult>,
) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (input: TInput) => {
      await ensureAntiforgery()
      return send(input)
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ownerKey(owner) }),
  })
}

export const useCreateProvider = (owner: ProviderOwner) =>
  useProviderMutation(owner, (body: CreateLlmProviderRequest) =>
    owner.kind === 'user'
      ? unwrap(api.POST('/api/llm-providers', { body }), 'add the provider')
      : unwrap(
          api.POST('/api/projects/{slug}/llm-providers', {
            params: { path: { slug: owner.slug } },
            body,
          }),
          'add the provider',
        ),
  )

export const useUpdateProvider = (owner: ProviderOwner) =>
  useProviderMutation(owner, ({ id, body }: { id: string; body: UpdateLlmProviderRequest }) =>
    unwrap(
      api.PUT('/api/llm-providers/{id}', { params: { path: { id } }, body }),
      'save the provider',
    ),
  )

export const useDeleteProvider = (owner: ProviderOwner) =>
  useProviderMutation(owner, (id: string) =>
    unwrap(
      api.DELETE('/api/llm-providers/{id}', { params: { path: { id } } }),
      'delete the provider',
    ),
  )

export const useTestProvider = () =>
  useMutation({
    mutationFn: async (id: string) => {
      await ensureAntiforgery()
      return unwrap(
        api.POST('/api/llm-providers/{id}/test', { params: { path: { id } } }),
        'run the test',
      )
    },
  })
