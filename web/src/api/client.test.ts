import type { MiddlewareCallbackParams } from 'openapi-fetch'
import { antiforgery, readCookie } from './client'

function run(method: string) {
  const request = new Request('http://localhost/api/auth/login', { method })
  const onRequest = antiforgery.onRequest as (p: MiddlewareCallbackParams) => Request
  return onRequest({ request } as MiddlewareCallbackParams)
}

describe('readCookie', () => {
  it('finds and decodes a cookie', () => {
    expect(readCookie('XSRF-TOKEN', 'a=1; XSRF-TOKEN=abc%2Bdef; b=2')).toBe('abc+def')
  })

  it('returns undefined when the cookie is missing', () => {
    expect(readCookie('XSRF-TOKEN', 'a=1')).toBeUndefined()
  })
})

describe('antiforgery middleware', () => {
  beforeEach(() => {
    document.cookie = 'XSRF-TOKEN=token-123; path=/'
  })

  it.each(['POST', 'PUT', 'PATCH', 'DELETE'])('adds the token header to %s', (method) => {
    expect(run(method).headers.get('X-XSRF-TOKEN')).toBe('token-123')
  })

  it('leaves GET requests alone', () => {
    expect(run('GET').headers.get('X-XSRF-TOKEN')).toBeNull()
  })
})
