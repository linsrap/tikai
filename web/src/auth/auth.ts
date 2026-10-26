import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, refreshAntiforgery } from '../api/client'
import type { components } from '../api/schema'

export type AuthStatus = components['schemas']['AuthStatus']
export type CurrentUser = components['schemas']['CurrentUser']

export const authStatusQuery = queryOptions({
  queryKey: ['auth', 'status'],
  queryFn: async (): Promise<AuthStatus> => {
    const { data, error } = await api.GET('/api/auth/status')
    if (error || !data) {
      throw new Error('Could not load the login status')
    }
    return data
  },
})

/** Where someone without a session goes: sign-up while no account exists, login afterwards. */
export function entryRouteFor(status: AuthStatus): '/' | '/login' | '/signup' {
  if (status.user) {
    return '/'
  }
  return status.signUpOpen ? '/signup' : '/login'
}

/** A problem+json body's best human-readable message. */
export function problemMessage(problem: unknown, fallback: string): string {
  if (problem && typeof problem === 'object') {
    const p = problem as { title?: string; detail?: string; errors?: Record<string, string[]> }
    const fieldErrors = p.errors ? Object.values(p.errors).flat() : []
    if (fieldErrors.length > 0) {
      return fieldErrors.join(' ')
    }
    return p.detail ?? p.title ?? fallback
  }
  return fallback
}

function useAuthMutation<TInput>(send: (input: TInput) => Promise<{ error?: unknown }>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (input: TInput) => {
      await refreshAntiforgery()
      const { error } = await send(input)
      if (error) {
        throw new Error(problemMessage(error, 'Something went wrong'))
      }
      // The antiforgery token is bound to the user, so it changes with every login and logout.
      await refreshAntiforgery()
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: authStatusQuery.queryKey }),
  })
}

export const useLogin = () =>
  useAuthMutation((body: { email: string; password: string }) =>
    api.POST('/api/auth/login', { body }),
  )

export const useSignUp = () =>
  useAuthMutation((body: { email: string; displayName: string; password: string }) =>
    api.POST('/api/auth/signup', { body }),
  )

export const useLogout = () => useAuthMutation(() => api.POST('/api/auth/logout'))
