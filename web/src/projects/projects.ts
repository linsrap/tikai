import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, ensureAntiforgery } from '../api/client'
import type { components } from '../api/schema'
import { unwrap } from '../api/result'

export type ProjectInfo = components['schemas']['ProjectInfo']
export type RepositoryInfo = components['schemas']['RepositoryInfo']
export type BranchInfo = components['schemas']['BranchInfo']
export type CredentialKind = components['schemas']['CredentialKind']
export type CredentialInput = components['schemas']['CredentialInput']
export type CreateRepositoryRequest = components['schemas']['CreateRepositoryRequest']
export type ProjectSettings = components['schemas']['ProjectSettingsInfo']
export type ReviewModels = components['schemas']['ReviewModels']

/** A URL name from a project name: "Tikai Web!" → "tikai-web". The server checks the result. */
export function suggestSlug(name: string): string {
  return name
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 40)
    .replace(/-+$/, '')
}

/** A ticket key prefix from a project name: initials of several words ("Tikai Web" → "TW"), else the first letters ("Tikai" → "TIK"). */
export function suggestKey(name: string): string {
  const words = name
    .normalize('NFKD')
    .toUpperCase()
    .split(/[^A-Z0-9]+/)
    .filter((w) => w.length > 0)
  const firstLetter = words.findIndex((w) => /^[A-Z]/.test(w))
  if (firstLetter < 0) {
    return ''
  }
  const usable = words.slice(firstLetter)
  const key = usable.length > 1 ? usable.map((w) => w[0]).join('') : usable[0].slice(0, 3)
  return key.slice(0, 10)
}

/** What the "add repository" form collects. */
export type RepositoryForm = {
  name: string
  source: 'local' | 'remote'
  remoteUrl: string
  defaultBranch: string
  credentialKind: CredentialKind
  username: string
  token: string
  privateKey: string
}

export const emptyRepositoryForm: RepositoryForm = {
  name: '',
  source: 'local',
  remoteUrl: '',
  defaultBranch: '',
  credentialKind: 'none',
  username: '',
  token: '',
  privateKey: '',
}

/** Only the fields that belong to the chosen credential kind are sent. */
export function toCredentialInput(form: RepositoryForm): CredentialInput | undefined {
  switch (form.credentialKind) {
    case 'https_token':
      return { kind: 'https_token', username: form.username.trim() || null, token: form.token }
    case 'ssh_key':
      return { kind: 'ssh_key', privateKey: form.privateKey }
    default:
      return undefined
  }
}

export function toCreateRepositoryRequest(form: RepositoryForm): CreateRepositoryRequest {
  const defaultBranch = form.defaultBranch.trim() || null
  if (form.source === 'local') {
    return { name: form.name.trim(), defaultBranch }
  }
  return {
    name: form.name.trim(),
    remoteUrl: form.remoteUrl.trim(),
    defaultBranch,
    credential: toCredentialInput(form) ?? null,
  }
}

export const projectsQuery = queryOptions({
  queryKey: ['projects'],
  queryFn: () => unwrap(api.GET('/api/projects'), 'load the projects'),
})

export const projectQuery = (slug: string) =>
  queryOptions({
    queryKey: ['projects', slug],
    queryFn: () =>
      unwrap(api.GET('/api/projects/{slug}', { params: { path: { slug } } }), 'load the project'),
  })

export const repositoriesQuery = (slug: string) =>
  queryOptions({
    queryKey: ['projects', slug, 'repositories'],
    queryFn: () =>
      unwrap(
        api.GET('/api/projects/{slug}/repositories', { params: { path: { slug } } }),
        'load the repositories',
      ),
    // Poll while a clone runs; live updates come with SignalR later.
    refetchInterval: (query) =>
      query.state.data?.some((r) => r.status === 'cloning') ? 2000 : false,
  })

export const branchesQuery = (slug: string, id: string) =>
  queryOptions({
    queryKey: ['projects', slug, 'repositories', id, 'branches'],
    queryFn: () =>
      unwrap(
        api.GET('/api/projects/{slug}/repositories/{id}/branches', {
          params: { path: { slug, id } },
        }),
        'load the branches',
      ),
  })

function useProjectMutation<TInput, TResult>(
  send: (input: TInput) => Promise<TResult>,
  invalidate: readonly unknown[],
) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (input: TInput) => {
      await ensureAntiforgery()
      return send(input)
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: invalidate }),
  })
}

export const useCreateProject = () =>
  useProjectMutation(
    (body: { name: string; slug: string; key: string }) =>
      unwrap(api.POST('/api/projects', { body }), 'create the project'),
    ['projects'],
  )

export const useDeleteProject = (slug: string) =>
  useProjectMutation(
    () =>
      unwrap(
        api.DELETE('/api/projects/{slug}', { params: { path: { slug } } }),
        'delete the project',
      ),
    ['projects'],
  )

export const useCreateRepository = (slug: string) =>
  useProjectMutation(
    (body: CreateRepositoryRequest) =>
      unwrap(
        api.POST('/api/projects/{slug}/repositories', { params: { path: { slug } }, body }),
        'add the repository',
      ),
    ['projects', slug],
  )

export const useFetchRepository = (slug: string) =>
  useProjectMutation(
    (id: string) =>
      unwrap(
        api.POST('/api/projects/{slug}/repositories/{id}/fetch', {
          params: { path: { slug, id } },
        }),
        'start the fetch',
      ),
    ['projects', slug],
  )

export const useSetCredential = (slug: string) =>
  useProjectMutation(
    ({ id, credential }: { id: string; credential: CredentialInput }) =>
      unwrap(
        api.PUT('/api/projects/{slug}/repositories/{id}/credential', {
          params: { path: { slug, id } },
          body: credential,
        }),
        'save the credential',
      ),
    ['projects', slug],
  )

export const useDeleteRepository = (slug: string) =>
  useProjectMutation(
    (id: string) =>
      unwrap(
        api.DELETE('/api/projects/{slug}/repositories/{id}', { params: { path: { slug, id } } }),
        'delete the repository',
      ),
    ['projects', slug],
  )

export const projectSettingsQuery = (slug: string) =>
  queryOptions({
    queryKey: ['projects', slug, 'settings'],
    queryFn: () =>
      unwrap(
        api.GET('/api/projects/{slug}/settings', { params: { path: { slug } } }),
        'load the settings',
      ),
  })

export const useSaveProjectSettings = (slug: string) =>
  useProjectMutation(
    (analysisModels: ReviewModels) =>
      unwrap(
        api.PUT('/api/projects/{slug}/settings', {
          params: { path: { slug } },
          body: { analysisModels },
        }),
        'save the settings',
      ),
    ['projects', slug, 'settings'],
  )
