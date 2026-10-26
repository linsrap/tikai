import createClient, { type Middleware } from 'openapi-fetch'
import type { paths } from './schema'

const unsafeMethods = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])

/** Reads a cookie the API set for scripts (the antiforgery token). */
export function readCookie(name: string, cookies = document.cookie): string | undefined {
  const prefix = `${name}=`
  const cookie = cookies.split('; ').find((c) => c.startsWith(prefix))
  return cookie ? decodeURIComponent(cookie.slice(prefix.length)) : undefined
}

/** Sends the XSRF-TOKEN cookie back as a header on unsafe requests; the API rejects them otherwise. */
export const antiforgery: Middleware = {
  onRequest({ request }) {
    if (unsafeMethods.has(request.method)) {
      const token = readCookie('XSRF-TOKEN')
      if (token) {
        request.headers.set('X-XSRF-TOKEN', token)
      }
    }
    return request
  },
}

export const api = createClient<paths>({ baseUrl: window.location.origin })
api.use(antiforgery)

/** Gets a fresh antiforgery token. Needed before the first unsafe request and after login, sign-up and logout. */
export async function refreshAntiforgery(): Promise<void> {
  const { response } = await api.GET('/api/auth/antiforgery')
  if (!response.ok) {
    throw new Error(`Could not get an antiforgery token (${response.status})`)
  }
}

/** Gets a token only if there is none yet, e.g. when the login cookie outlived the browser session. */
export async function ensureAntiforgery(): Promise<void> {
  if (!readCookie('XSRF-TOKEN')) {
    await refreshAntiforgery()
  }
}
